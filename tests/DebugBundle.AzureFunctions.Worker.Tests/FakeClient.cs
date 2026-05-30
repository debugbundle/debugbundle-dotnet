using DebugBundle;

namespace DebugBundle.AzureFunctions.Worker.Tests;

internal sealed class FakeClient : IDebugBundleClient
{
    public List<(Exception Exception, IDictionary<string, object?>? Context)> Exceptions { get; } = new();
    public List<(DebugBundleRequestInfo Request, DebugBundleResponseInfo Response, IDictionary<string, object?>? Context)> Requests { get; } = new();
    public DebugBundleStatus Status { get; } = DebugBundleStatus.Healthy;
    public DateTimeOffset? LastEventAt { get; }

    public void CaptureException(Exception? exception, IDictionary<string, object?>? context = null)
    {
        if (exception != null)
        {
            Exceptions.Add((exception, context));
        }
    }

    public void CaptureRequest(DebugBundleRequestInfo? request, DebugBundleResponseInfo? response, IDictionary<string, object?>? context = null)
    {
        if (request != null && response != null)
        {
            Requests.Add((request, response, context));
        }
    }

    public void CaptureError(Exception? exception, IDictionary<string, object?>? context = null) => CaptureException(exception, context);
    public void CaptureLog(string? message, DebugBundleLogLevel level = DebugBundleLogLevel.Information, IDictionary<string, object?>? context = null) { }
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
