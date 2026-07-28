namespace DebugBundle;

internal sealed class StaticDebugBundleClient : IDebugBundleClient
{
    public DebugBundleStatus Status => DebugBundle.Status;
    public DateTimeOffset? LastEventAt => DebugBundle.LastEventAt;
    public void CaptureException(Exception? exception, IDictionary<string, object?>? context = null) =>
        DebugBundle.CaptureException(exception, context);
    public void CaptureError(Exception? exception, IDictionary<string, object?>? context = null) =>
        DebugBundle.CaptureError(exception, context);
    public void CaptureLog(string? message, DebugBundleLogLevel level = DebugBundleLogLevel.Information, IDictionary<string, object?>? context = null) =>
        DebugBundle.CaptureLog(message, level, context);
    public void CaptureRequest(DebugBundleRequestInfo? request, DebugBundleResponseInfo? response, IDictionary<string, object?>? context = null) =>
        DebugBundle.CaptureRequest(request, response, context);
    public void CaptureMessage(string? message, DebugBundleLogLevel level = DebugBundleLogLevel.Information, IDictionary<string, object?>? context = null) =>
        DebugBundle.CaptureMessage(message, level, context);
    public void SetContext(string key, object? value) => DebugBundle.SetContext(key, value);
    public DebugBundleScope BeginScope(IDictionary<string, object?> values) => DebugBundle.BeginScope(values);
    public void SetUserHash(string userHash) => DebugBundle.SetUserHash(userHash);
    public void SetTraceId(string traceId) => DebugBundle.SetTraceId(traceId);
    public void SetRequestId(string requestId) => DebugBundle.SetRequestId(requestId);
    public void Probe(string label, object? data, ProbeOptions? options = null) => DebugBundle.Probe(label, data, options);
    public void Probe(string label, Func<object?> data, ProbeOptions? options = null) => DebugBundle.Probe(label, data, options);
    public Task FlushAsync(CancellationToken cancellationToken = default) => DebugBundle.FlushAsync(cancellationToken);
}
