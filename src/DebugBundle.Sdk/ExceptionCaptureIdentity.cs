namespace DebugBundle;

internal sealed class ExceptionCaptureIdentity
{
    private sealed class Entry
    {
        internal readonly WeakReference<Exception> Error;
        internal readonly string? Scope;
        internal readonly bool Automatic;
        internal readonly long At;
        internal Entry(WeakReference<Exception> error, string? scope, bool automatic, long at)
        { Error = error; Scope = scope; Automatic = automatic; At = at; }
    }
    private readonly List<Entry> _entries = new();
    private readonly object _gate = new();

    internal bool Admit(Exception error, string? scope, bool automatic, bool accepted = false)
    {
        if (scope == null) return true;
        if (!Monitor.TryEnter(_gate)) return true;
        try
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp() / (System.Diagnostics.Stopwatch.Frequency / 1000);
            _entries.RemoveAll(entry => !entry.Error.TryGetTarget(out _) || now - entry.At >= 30_000);
            foreach (var entry in _entries)
                if (entry.Error.TryGetTarget(out var previous) && ReferenceEquals(previous, error) && entry.Scope == scope)
                {
                    if (!accepted && automatic != entry.Automatic) { _entries.Remove(entry); return false; }
                    if (accepted) _entries.Remove(entry);
                    break;
                }
            if (accepted)
            {
                if (_entries.Count == 128) _entries.RemoveAt(0);
                _entries.Add(new(new(error), scope, automatic, now));
            }
            return true;
        }
        finally { Monitor.Exit(_gate); }
    }
}
