using System.Security.Cryptography;
using System.Text;

namespace DebugBundle;

internal sealed class SuppressionTracker
{
    private static readonly TimeSpan SuppressionWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LoopWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SilenceReset = TimeSpan.FromSeconds(60);
    private const int LoopThreshold = 10;

    private readonly object _sync = new();
    private readonly Dictionary<string, SuppressionState> _states = new(StringComparer.Ordinal);

    public bool ShouldCapture(string fingerprint, DateTimeOffset now)
    {
        lock (_sync)
        {
            if (!_states.TryGetValue(fingerprint, out var state) || now - state.LastSeen > SilenceReset)
            {
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
