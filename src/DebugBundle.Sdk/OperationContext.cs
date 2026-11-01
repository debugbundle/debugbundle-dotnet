using System.Diagnostics;

namespace DebugBundle;

public sealed class DebugBundleOperationOptions
{
    public string Kind { get; set; } = "unknown";
    public string? Name { get; set; }
    /// <summary>The configured limit, never time remaining.</summary>
    public TimeSpan? Limit { get; set; }
}

// Separate capability interface preserves existing IDebugBundleClient implementors.
public interface IDebugBundleOperationClient : IDebugBundleClient
{
    DebugBundleOperationScope BeginOperation(DebugBundleOperationOptions? options = null);
}

internal sealed class OperationRegistry { internal int Closed; }
internal sealed class OperationLease
{
    internal readonly OperationRegistry Registry;
    internal readonly OperationLease? Parent;
    internal readonly string Id, Kind;
    internal readonly string? Name;
    internal readonly double? Limit;
    internal readonly long Started = Stopwatch.GetTimestamp();
    internal int Ended;
    internal OperationLease(OperationRegistry registry, OperationLease? parent, string kind, string? name, double? limit)
    { Registry = registry; Parent = parent; Kind = kind; Name = name; Limit = limit; Id = Guid.NewGuid().ToString("D"); }
    internal bool Active(OperationRegistry registry)
    {
        if (!ReferenceEquals(Registry, registry) || Volatile.Read(ref registry.Closed) != 0) return false;
        var depth = 0;
        for (OperationLease? owner = this; owner != null; owner = owner.Parent)
            if (++depth > 32 || Volatile.Read(ref owner.Ended) != 0) return false;
        return true;
    }
}
internal static class OperationContext
{
    private static readonly AsyncLocal<OperationLease?> Storage = new();
    internal static OperationLease? Current { get => Storage.Value; set => Storage.Value = value; }
}

public sealed class DebugBundleOperationScope : IDisposable
{
    private readonly OperationLease? _lease, _previous;
    private int _disposed;
    internal DebugBundleOperationScope(OperationLease? lease)
    { _lease = lease; _previous = OperationContext.Current; if (lease != null) OperationContext.Current = lease; }
    internal static DebugBundleOperationScope None() => new(null);
    public string? OperationId => _lease?.Id;
    public void Dispose()
    {
        if (_lease == null || Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Volatile.Write(ref _lease.Ended, 1);
        if (ReferenceEquals(OperationContext.Current, _lease)) OperationContext.Current = _previous;
    }
}

public static class DebugBundleOperationExtensions
{
    public static DebugBundleOperationScope BeginOperation(this IDebugBundleClient? client, DebugBundleOperationOptions? options = null) =>
        (client as IDebugBundleOperationClient)?.BeginOperation(options) ?? DebugBundleOperationScope.None();
    public static T RunWithOperationContext<T>(this IDebugBundleClient? client, DebugBundleOperationOptions options, Func<T> callback)
    { using var scope = client.BeginOperation(options); return callback == null ? default! : callback(); }
    public static void RunWithOperationContext(this IDebugBundleClient? client, DebugBundleOperationOptions options, Action callback)
    { using var scope = client.BeginOperation(options); callback?.Invoke(); }
    public static async Task<T> RunWithOperationContextAsync<T>(this IDebugBundleClient? client, DebugBundleOperationOptions options, Func<Task<T>> callback)
    { using var scope = client.BeginOperation(options); return callback == null ? default! : await callback().ConfigureAwait(false); }
    public static async Task RunWithOperationContextAsync(this IDebugBundleClient? client, DebugBundleOperationOptions options, Func<Task> callback)
    { using var scope = client.BeginOperation(options); if (callback != null) await callback().ConfigureAwait(false); }
}
