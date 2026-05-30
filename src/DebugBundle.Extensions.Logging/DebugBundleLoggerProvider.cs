using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DebugBundle.Logging;

public sealed class DebugBundleLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly IServiceProvider _serviceProvider;
    private readonly DebugBundleLoggerOptions _options;
    private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();
    private bool _disposed;

    public DebugBundleLoggerProvider(IServiceProvider serviceProvider, IOptions<DebugBundleLoggerOptions>? options = null)
    {
        _serviceProvider = serviceProvider;
        _options = options?.Value ?? new DebugBundleLoggerOptions();
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new DebugBundleLogger(categoryName, _serviceProvider, _options, () => _scopeProvider, () => _disposed);
    }

    public void SetScopeProvider(IExternalScopeProvider scopeProvider)
    {
        _scopeProvider = scopeProvider;
    }

    public void Dispose()
    {
        _disposed = true;
    }
}

internal sealed class DebugBundleLogger : ILogger
{
    private static readonly AsyncLocal<bool> CaptureGuard = new();
    private readonly string _categoryName;
    private readonly IServiceProvider _serviceProvider;
    private readonly DebugBundleLoggerOptions _options;
    private readonly Func<IExternalScopeProvider> _scopeProvider;
    private readonly Func<bool> _isDisposed;

    public DebugBundleLogger(
        string categoryName,
        IServiceProvider serviceProvider,
        DebugBundleLoggerOptions options,
        Func<IExternalScopeProvider> scopeProvider,
        Func<bool> isDisposed)
    {
        _categoryName = categoryName;
        _serviceProvider = serviceProvider;
        _options = options;
        _scopeProvider = scopeProvider;
        _isDisposed = isDisposed;
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
    {
        return _scopeProvider().Push(state);
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return !_isDisposed()
            && logLevel != LogLevel.None
            && logLevel >= _options.MinimumLevel
            && !IsExcludedCategory(_categoryName);
    }

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel) || CaptureGuard.Value)
        {
            return;
        }

        var client = _serviceProvider.GetService<IDebugBundleClient>();
        if (client == null)
        {
            return;
        }

        CaptureGuard.Value = true;
        try
        {
            var fields = BuildFields(logLevel, eventId, state, exception, formatter);
            client.CaptureLog(RenderMessage(state, exception, formatter), MapLevel(logLevel), fields);
        }
        catch
        {
            // Logging providers must never throw into application logging paths.
        }
        finally
        {
            CaptureGuard.Value = false;
        }
    }

    private Dictionary<string, object?> BuildFields<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["logger.category"] = _categoryName,
            ["logger.level"] = logLevel.ToString(),
            ["event_id.id"] = eventId.Id,
            ["event_id.name"] = eventId.Name,
            ["message"] = RenderMessage(state, exception, formatter),
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("O")
        };

        if (Activity.Current != null)
        {
            fields["activity_trace_id"] = Activity.Current.TraceId.ToString();
            fields["activity_span_id"] = Activity.Current.SpanId.ToString();
        }

        AddStructuredState(fields, state);
        AddScopes(fields);
        AddException(fields, exception);
        return fields;
    }

    private void AddStructuredState<TState>(IDictionary<string, object?> fields, TState state)
    {
        if (state is not IEnumerable<KeyValuePair<string, object?>> structured)
        {
            if (state != null)
            {
                fields["state"] = state;
            }

            return;
        }

        var attributes = new Dictionary<string, object?>(StringComparer.Ordinal);
        var count = 0;
        foreach (var item in structured)
        {
            if (item.Key == "{OriginalFormat}")
            {
                fields["message_template"] = item.Value;
                continue;
            }

            if (count >= Math.Max(1, _options.MaxStructuredFields))
            {
                attributes["_truncated"] = "additional structured fields omitted";
                break;
            }

            attributes[item.Key] = item.Value;
            count++;
        }

        if (attributes.Count > 0)
        {
            fields["structured_state"] = attributes;
        }
    }

    private void AddScopes(IDictionary<string, object?> fields)
    {
        if (!_options.IncludeScopes)
        {
            return;
        }

        var scopes = new List<object?>();
        _scopeProvider().ForEachScope((scope, target) => target.Add(NormalizeScope(scope)), scopes);
        if (scopes.Count > 0)
        {
            fields["scopes"] = scopes;
        }
    }

    private void AddException(IDictionary<string, object?> fields, Exception? exception)
    {
        if (!_options.IncludeExceptionDetails || exception == null)
        {
            return;
        }

        fields["exception"] = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = exception.GetType().FullName,
            ["message"] = exception.Message,
            ["stack"] = exception.ToString(),
            ["hresult"] = exception.HResult
        };
    }

    private object? NormalizeScope(object? scope)
    {
        if (scope is IEnumerable<KeyValuePair<string, object?>> values)
        {
            return values.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        }

        return scope;
    }

    private bool IsExcludedCategory(string categoryName)
    {
        foreach (var prefix in _options.ExcludedCategoryPrefixes)
        {
            if (categoryName.Equals(prefix, StringComparison.Ordinal) || categoryName.StartsWith(prefix + ".", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string RenderMessage<TState>(TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        try
        {
            var message = formatter(state, exception);
            return string.IsNullOrWhiteSpace(message) ? exception?.Message ?? string.Empty : message;
        }
        catch
        {
            return exception?.Message ?? string.Empty;
        }
    }

    private static DebugBundleLogLevel MapLevel(LogLevel logLevel)
    {
        return logLevel switch
        {
            LogLevel.Trace => DebugBundleLogLevel.Trace,
            LogLevel.Debug => DebugBundleLogLevel.Debug,
            LogLevel.Information => DebugBundleLogLevel.Information,
            LogLevel.Warning => DebugBundleLogLevel.Warning,
            LogLevel.Error => DebugBundleLogLevel.Error,
            LogLevel.Critical => DebugBundleLogLevel.Critical,
            _ => DebugBundleLogLevel.Information
        };
    }
}
