using System.Security.Cryptography;
using System.Text;

namespace DebugBundle;

internal sealed class SuppressionTracker
{
    private static readonly TimeSpan SuppressionWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LoopWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SilenceReset = TimeSpan.FromSeconds(60);
    private const int LoopThreshold = 10;
    private const int MaxTrackedFingerprints = 2_048;

    private readonly object _sync = new();
    private readonly Dictionary<string, SuppressionState> _states = new(StringComparer.Ordinal);
    private int _overflowCount;
    private DateTimeOffset _overflowFirst;
    private DateTimeOffset _overflowLast;

    internal int TrackedCount { get { lock (_sync) return _states.Count; } }

    public bool ShouldCapture(string fingerprint, DateTimeOffset now)
    {
        if (!Monitor.TryEnter(_sync)) return true;
        try
        {
            if (!_states.TryGetValue(fingerprint, out var state) || now - state.LastSeen > SilenceReset)
            {
                if (!_states.ContainsKey(fingerprint) && _states.Count >= MaxTrackedFingerprints)
                {
                    var oldest = _states.First();
                    if (oldest.Value.Suppressed > 0)
                    {
                        if (_overflowCount == 0) _overflowFirst = oldest.Value.FirstSeen;
                        _overflowCount = (int)Math.Min(int.MaxValue, (long)_overflowCount + oldest.Value.Suppressed);
                        _overflowLast = oldest.Value.LastSeen;
                    }
                    _states.Remove(oldest.Key);
                }
                state = new SuppressionState(now);
                _states[fingerprint] = state;
            }

            if (now - state.WindowStarted > SuppressionWindow)
            {
                state.WindowStarted = now;
                state.Delivered = 0;
                state.LoopMode = false;
                state.RecentSeen.Clear();
            }

            state.LastSeen = now;
            state.RecentSeen.Add(now);
            state.RecentSeen.RemoveAll(value => value < now - LoopWindow);
            if (state.RecentSeen.Count > LoopThreshold)
            {
                state.LoopMode = true;
            }

            if (!state.LoopMode && state.Delivered < 3)
            {
                state.Delivered++;
                return true;
            }

            state.Suppressed++;
            return false;
        }
        finally { Monitor.Exit(_sync); }
    }

    public IReadOnlyList<SuppressionAggregate> DrainAggregates(DateTimeOffset now)
    {
        lock (_sync)
        {
            var aggregates = new List<SuppressionAggregate>();
            foreach (var item in _states.ToArray())
            {
                var state = item.Value;
                if (now - state.LastSeen > SilenceReset)
                {
                    _states.Remove(item.Key);
                    continue;
                }

                if (state.Suppressed == 0)
                {
                    continue;
                }

                aggregates.Add(new SuppressionAggregate
                {
                    Fingerprint = item.Key,
                    SuppressedCount = state.Suppressed,
                    FirstSeen = state.FirstSeen,
                    LastSeen = state.LastSeen,
                    WindowSeconds = Math.Max(1, (int)SuppressionWindow.TotalSeconds),
                    LoopMode = state.LoopMode
                });
                state.Suppressed = 0;
                state.LastAggregateAt = now;
            }

            if (_overflowCount > 0)
            {
                aggregates.Add(new SuppressionAggregate
                {
                    Fingerprint = Fingerprint("suppression_state_pressure", new Dictionary<string, object?>()),
                    SuppressedCount = _overflowCount,
                    FirstSeen = _overflowFirst,
                    LastSeen = _overflowLast,
                    WindowSeconds = (int)SuppressionWindow.TotalSeconds
                });
                _overflowCount = 0;
            }

            return aggregates;
        }
    }

    public static string Fingerprint(string eventType, IReadOnlyDictionary<string, object?> payload)
    {
        var stable = eventType + "|" + Extract(payload, "name") + "|" + Extract(payload, "message") + "|" + Extract(payload, "method") + "|" + Extract(payload, "path");
        using var sha256 = SHA256.Create();
        var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(stable));
        return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static string Extract(IReadOnlyDictionary<string, object?> payload, string key)
    {
        return payload.TryGetValue(key, out var value) ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty : string.Empty;
    }

    private sealed class SuppressionState
    {
        public SuppressionState(DateTimeOffset now)
        {
            FirstSeen = now;
            LastSeen = now;
            WindowStarted = now;
        }

        public DateTimeOffset FirstSeen { get; }
        public DateTimeOffset LastSeen { get; set; }
        public DateTimeOffset WindowStarted { get; set; }
        public DateTimeOffset LastAggregateAt { get; set; }
        public int Delivered { get; set; }
        public int Suppressed { get; set; }
        public bool LoopMode { get; set; }
        public List<DateTimeOffset> RecentSeen { get; } = new();
    }
}

internal sealed class SuppressionAggregate
{
    public string Fingerprint { get; set; } = string.Empty;
    public int SuppressedCount { get; set; }
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public int WindowSeconds { get; set; }
    public bool LoopMode { get; set; }
}
