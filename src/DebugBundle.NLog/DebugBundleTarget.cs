using NLog;
using NLog.Targets;

namespace DebugBundle.NLog;

[Target("DebugBundle")]
public class DebugBundleTarget : Target
{
    private readonly IDebugBundleClient _client;

    public DebugBundleTarget()
        : this(new StaticDebugBundleClient())
    {
    }

    public DebugBundleTarget(IDebugBundleClient client)
    {
        _client = client;
    }

    protected override void Write(LogEventInfo logEvent)
    {
        if (logEvent == null)
        {
            return;
        }

        try
        {
            var context = BuildContext(logEvent);
            if (logEvent.Exception != null)
            {
                _client.CaptureException(logEvent.Exception, context);
            }

            _client.CaptureLog(logEvent.FormattedMessage, MapLevel(logEvent.Level), context);
        }
        catch
        {
        }
    }

    private static Dictionary<string, object?> BuildContext(LogEventInfo logEvent)
    {
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in logEvent.Properties)
        {
            if (property.Key != null)
            {
                properties[property.Key.ToString()!] = property.Value;
            }
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["logger"] = logEvent.LoggerName,
            ["provider"] = "nlog",
            ["level"] = logEvent.Level.Name,
            ["message_template"] = logEvent.Message,
            ["properties"] = properties,
            ["exception_type"] = logEvent.Exception?.GetType().FullName,
            ["exception_message"] = logEvent.Exception?.Message
        };
    }

    private static DebugBundleLogLevel MapLevel(LogLevel level)
    {
        if (level == LogLevel.Trace)
        {
            return DebugBundleLogLevel.Trace;
        }

        if (level == LogLevel.Debug)
        {
            return DebugBundleLogLevel.Debug;
        }

        if (level == LogLevel.Info)
        {
            return DebugBundleLogLevel.Information;
        }

        if (level == LogLevel.Warn)
        {
            return DebugBundleLogLevel.Warning;
        }

        return level == LogLevel.Fatal ? DebugBundleLogLevel.Critical : DebugBundleLogLevel.Error;
    }

    private sealed class StaticDebugBundleClient : IDebugBundleClient
    {
        public DebugBundleStatus Status => DebugBundle.Status;
        public DateTimeOffset? LastEventAt => DebugBundle.LastEventAt;
        public void CaptureException(Exception? exception, IDictionary<string, object?>? context = null) => DebugBundle.CaptureException(exception, context);
        public void CaptureError(Exception? exception, IDictionary<string, object?>? context = null) => DebugBundle.CaptureError(exception, context);
        public void CaptureLog(string? message, DebugBundleLogLevel level = DebugBundleLogLevel.Information, IDictionary<string, object?>? context = null) => DebugBundle.CaptureLog(message, level, context);
        public void CaptureRequest(DebugBundleRequestInfo? request, DebugBundleResponseInfo? response, IDictionary<string, object?>? context = null) => DebugBundle.CaptureRequest(request, response, context);
        public void CaptureMessage(string? message, DebugBundleLogLevel level = DebugBundleLogLevel.Information, IDictionary<string, object?>? context = null) => DebugBundle.CaptureMessage(message, level, context);
        public void SetContext(string key, object? value) => DebugBundle.SetContext(key, value);
        public DebugBundleScope BeginScope(IDictionary<string, object?> values) => DebugBundle.BeginScope(values);
        public void SetUserHash(string userHash) => DebugBundle.SetUserHash(userHash);
        public void SetTraceId(string traceId) => DebugBundle.SetTraceId(traceId);
        public void SetRequestId(string requestId) => DebugBundle.SetRequestId(requestId);
        public void Probe(string label, object? data, ProbeOptions? options = null) => DebugBundle.Probe(label, data, options);
        public void Probe(string label, Func<object?> data, ProbeOptions? options = null) => DebugBundle.Probe(label, data, options);
        public Task FlushAsync(CancellationToken cancellationToken = default) => DebugBundle.FlushAsync(cancellationToken);
    }
}
