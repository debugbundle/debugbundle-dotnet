using Serilog.Core;
using Serilog.Events;

namespace DebugBundle.Serilog;

public sealed class DebugBundleSink : ILogEventSink
{
    private readonly IDebugBundleClient _client;

    public DebugBundleSink()
        : this(new StaticDebugBundleClient())
    {
    }

    public DebugBundleSink(IDebugBundleClient client)
    {
        _client = client;
    }

    public void Emit(LogEvent logEvent)
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

            _client.CaptureLog(logEvent.RenderMessage(), MapLevel(logEvent.Level), context);
        }
        catch
        {
        }
    }

    private static Dictionary<string, object?> BuildContext(LogEvent logEvent)
    {
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in logEvent.Properties)
        {
            properties[property.Key] = property.Value.ToString();
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["logger"] = "serilog",
            ["message_template"] = logEvent.MessageTemplate.Text,
            ["timestamp"] = logEvent.Timestamp.ToString("O"),
            ["properties"] = properties,
            ["exception_type"] = logEvent.Exception?.GetType().FullName,
            ["exception_message"] = logEvent.Exception?.Message
        };
    }

    private static DebugBundleLogLevel MapLevel(LogEventLevel level)
    {
        return level switch
        {
            LogEventLevel.Verbose => DebugBundleLogLevel.Trace,
            LogEventLevel.Debug => DebugBundleLogLevel.Debug,
            LogEventLevel.Information => DebugBundleLogLevel.Information,
            LogEventLevel.Warning => DebugBundleLogLevel.Warning,
            LogEventLevel.Error => DebugBundleLogLevel.Error,
            LogEventLevel.Fatal => DebugBundleLogLevel.Critical,
            _ => DebugBundleLogLevel.Information
        };
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
