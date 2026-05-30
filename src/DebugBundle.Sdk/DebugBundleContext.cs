using System.Collections.Immutable;

namespace DebugBundle;

public sealed class DebugBundleScope : IDisposable
{
    private readonly ImmutableDictionary<string, object?>? _previous;
    private bool _disposed;

    internal DebugBundleScope(IDictionary<string, object?> values)
    {
        _previous = DebugBundleContext.Current;
        DebugBundleContext.Current = (_previous ?? ImmutableDictionary<string, object?>.Empty).SetItems(values);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        DebugBundleContext.Current = _previous;
        _disposed = true;
    }
}

internal static class DebugBundleContext
{
    private static readonly AsyncLocal<ImmutableDictionary<string, object?>?> AsyncContext = new();

    public static ImmutableDictionary<string, object?>? Current
    {
        get => AsyncContext.Value;
        set => AsyncContext.Value = value;
    }

    public static DebugBundleScope BeginScope(IDictionary<string, object?> values) => new(values);
}
