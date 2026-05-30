using log4net.Appender;
using log4net.Core;

namespace DebugBundle.Log4Net;

public sealed class DebugBundleAppender : AppenderSkeleton
{
    private readonly IDebugBundleClient _client;

    public DebugBundleAppender()
        : this(new StaticDebugBundleClient())
    {
    }

    public DebugBundleAppender(IDebugBundleClient client)
    {
        _client = client;
    }

    protected override void Append(LoggingEvent loggingEvent)
    {
        if (loggingEvent == null)
        {
            return;
        }

        try
        {
            var context = BuildContext(loggingEvent);
            if (loggingEvent.ExceptionObject != null)
            {
                _client.CaptureException(loggingEvent.ExceptionObject, context);
            }

            _client.CaptureLog(loggingEvent.RenderedMessage, MapLevel(loggingEvent.Level), context);
        }
        catch
        {
        }
    }

    private static Dictionary<string, object?> BuildContext(LoggingEvent loggingEvent)
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["logger"] = loggingEvent.LoggerName,
            ["provider"] = "log4net",
            ["level"] = loggingEvent.Level?.DisplayName,
            ["thread"] = loggingEvent.ThreadName,
            ["exception_type"] = loggingEvent.ExceptionObject?.GetType().FullName,
            ["exception_message"] = loggingEvent.ExceptionObject?.Message
        };
    }

    private static DebugBundleLogLevel MapLevel(Level? level)
    {
        if (level == null)
        {
            return DebugBundleLogLevel.Information;
        }

        if (level >= Level.Fatal)
        {
            return DebugBundleLogLevel.Critical;
        }

        if (level >= Level.Error)
        {
            return DebugBundleLogLevel.Error;
        }

        if (level >= Level.Warn)
        {
            return DebugBundleLogLevel.Warning;
        }

        if (level >= Level.Info)
        {
            return DebugBundleLogLevel.Information;
        }

        return level >= Level.Debug ? DebugBundleLogLevel.Debug : DebugBundleLogLevel.Trace;
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
