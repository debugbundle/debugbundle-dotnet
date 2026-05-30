namespace DebugBundle;

public interface IDebugBundleClient
{
    DebugBundleStatus Status { get; }
    DateTimeOffset? LastEventAt { get; }
    void CaptureException(Exception? exception, IDictionary<string, object?>? context = null);
    void CaptureError(Exception? exception, IDictionary<string, object?>? context = null);
    void CaptureLog(string? message, DebugBundleLogLevel level = DebugBundleLogLevel.Information, IDictionary<string, object?>? context = null);
    void CaptureRequest(DebugBundleRequestInfo? request, DebugBundleResponseInfo? response, IDictionary<string, object?>? context = null);
    void CaptureMessage(string? message, DebugBundleLogLevel level = DebugBundleLogLevel.Information, IDictionary<string, object?>? context = null);
    void SetContext(string key, object? value);
    DebugBundleScope BeginScope(IDictionary<string, object?> values);
    void SetUserHash(string userHash);
    void SetTraceId(string traceId);
    void SetRequestId(string requestId);
    void Probe(string label, object? data, ProbeOptions? options = null);
    void Probe(string label, Func<object?> data, ProbeOptions? options = null);
    Task FlushAsync(CancellationToken cancellationToken = default);
}
