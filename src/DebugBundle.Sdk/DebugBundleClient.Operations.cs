using System.Diagnostics;
using DebugBundle.Redaction;

namespace DebugBundle;

public sealed partial class DebugBundleClient
{
    private readonly OperationRegistry _operations = new();
    public DebugBundleOperationScope BeginOperation(DebugBundleOperationOptions? options = null)
    {
        try
        {
            if (!_options.Enabled || Volatile.Read(ref _operations.Closed) != 0) return DebugBundleOperationScope.None();
            options ??= new DebugBundleOperationOptions();
            var parent = OperationContext.Current;
            if (parent?.Active(_operations) != true) parent = null;
            var depth = 0;
            for (var owner = parent; owner != null; owner = owner.Parent) if (++depth >= 31) return DebugBundleOperationScope.None();
            var kind = options.Kind is "request" or "command" or "job" ? options.Kind : "unknown";
            string? name = null;
            if (!string.IsNullOrEmpty(options.Name) && options.Name!.Length <= 4096)
                name = ExceptionEvidence.Clip((string)TelemetryPrivacy.Protect(options.Name, _options.RedactFields)!, 128);
            var limit = options.Limit is { } configured && configured >= TimeSpan.Zero ? configured.TotalMilliseconds : (double?)null;
            return new DebugBundleOperationScope(new OperationLease(_operations, parent, kind, name, limit));
        }
        catch { return DebugBundleOperationScope.None(); }
    }
    internal Dictionary<string, object?>? CurrentExecution()
    {
        var owner = OperationContext.Current;
        if (!_options.CaptureExceptionEvidence || !_evidence.Supports("exception_evidence_v1") || owner?.Active(_operations) != true) return null;
        return new Dictionary<string, object?>
        {
            ["version"] = 1,
            ["kind"] = owner.Kind,
            ["name"] = owner.Name,
            ["elapsed_ms"] = Math.Max(0, (Stopwatch.GetTimestamp() - owner.Started) * 1000d / Stopwatch.Frequency),
            ["limit_ms"] = owner.Limit
        };
    }
    public T RunWithOperationContext<T>(DebugBundleOperationOptions options, Func<T> callback) => DebugBundleOperationExtensions.RunWithOperationContext(this, options, callback);
    public void RunWithOperationContext(DebugBundleOperationOptions options, Action callback) => DebugBundleOperationExtensions.RunWithOperationContext(this, options, callback);
    public Task<T> RunWithOperationContextAsync<T>(DebugBundleOperationOptions options, Func<Task<T>> callback) => DebugBundleOperationExtensions.RunWithOperationContextAsync(this, options, callback);
    public Task RunWithOperationContextAsync(DebugBundleOperationOptions options, Func<Task> callback) => DebugBundleOperationExtensions.RunWithOperationContextAsync(this, options, callback);
}
