using DebugBundle;

namespace DebugBundle.Extensions.Logging.Tests;

internal sealed class FakeClient : IDebugBundleClient
{
    public List<(string Message, DebugBundleLogLevel Level, IDictionary<string, object?>? Context)> Logs { get; } = new();
    public DebugBundleStatus Status { get; } = DebugBundleStatus.Healthy;
    public DateTimeOffset? LastEventAt { get; }

    public void CaptureException(Exception? exception, IDictionary<string, object?>? context = null) { }
    public void CaptureError(Exception? exception, IDictionary<string, object?>? context = null) { }

    public void CaptureLog(string? message, DebugBundleLogLevel level = DebugBundleLogLevel.Information, IDictionary<string, object?>? context = null)
    {
        Logs.Add((message ?? string.Empty, level, context));
    }

    public void CaptureRequest(DebugBundleRequestInfo? request, DebugBundleResponseInfo? response, IDictionary<string, object?>? context = null) { }
    public void CaptureMessage(string? message, DebugBundleLogLevel level = DebugBundleLogLevel.Information, IDictionary<string, object?>? context = null) { }
    public void SetContext(string key, object? value) { }
    public DebugBundleScope BeginScope(IDictionary<string, object?> values) => global::DebugBundle.DebugBundle.BeginScope(values);
    public void SetUserHash(string userHash) { }
    public void SetTraceId(string traceId) { }
    public void SetRequestId(string requestId) { }
    public void Probe(string label, object? data, ProbeOptions? options = null) { }
    public void Probe(string label, Func<object?> data, ProbeOptions? options = null) { }
    public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
