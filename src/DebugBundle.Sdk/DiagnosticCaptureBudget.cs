using System.Text;
using System.Text.Json;

namespace DebugBundle;

// All access belongs to the client lock. Allowances survive acknowledgement;
// pending receipts keep an expired offline window from renewing its allowance.
internal sealed class DiagnosticCaptureBudget
{
    private sealed class Window
    {
        public DateTimeOffset Started, FailureAt;
        public string FailureId = "";
        public HashSet<string> Attempts = new(StringComparer.Ordinal);
        public int Observations, Pending;
        public bool Resource, Ambiguous;
        public Window Copy() => new() { Started = Started, FailureAt = FailureAt, FailureId = FailureId, Attempts = new HashSet<string>(Attempts, StringComparer.Ordinal), Observations = Observations, Pending = Pending, Resource = Resource, Ambiguous = Ambiguous };
    }
    private sealed class Receipt
    {
        internal string Key = "", Kind = "", Attempt = "";
        internal int Bytes, Cost;
    }
    private readonly Dictionary<string, Window> _windows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Receipt> _pending = new(StringComparer.Ordinal);
    private int _held;
    internal int PendingCount => _pending.Count;
    internal int Bytes => _held + _windows.Sum(item => WindowBytes(item.Key, item.Value));
    private static string Text(Dictionary<string, object?> map, string key) => map.TryGetValue(key, out var value) ? value as string ?? "" : "";
    private static string Key(Dictionary<string, object?> payload)
    {
        var scope = (Dictionary<string, object?>)payload["scope"]!;
        var kind = Text(scope, "kind");
        return Text(payload, "failure_ref") + ":" + kind + ":" + Text(scope, kind == "page" ? "page_id" : "operation_id");
    }
    private static int WindowBytes(string key, Window window) => Encoding.UTF8.GetByteCount(key) + JsonSerializer.SerializeToUtf8Bytes(new { window.Started, window.FailureAt, window.FailureId, Attempts = window.Attempts.OrderBy(value => value, StringComparer.Ordinal), window.Observations, window.Pending, window.Resource, window.Ambiguous }).Length;
    private void Prune(DateTimeOffset now)
    {
        foreach (var item in _windows.Where(item => item.Value.Pending == 0 && now >= item.Value.Started.AddMinutes(5)).ToArray()) _windows.Remove(item.Key);
    }
    internal bool Register(Dictionary<string, object?> reference, string eventId, DateTimeOffset now)
    {
        Prune(now); var key = Key(reference); var exists = _windows.TryGetValue(key, out var previous);
        if (exists && previous!.FailureId.Length > 0 && previous.FailureId != eventId) { previous.Ambiguous = true; return false; }
        if (!exists && _windows.Count >= 50) return false;
        var window = previous?.Copy() ?? new Window { Started = now };
        window.FailureId = eventId; if (window.FailureAt == default) window.FailureAt = now;
        if (Bytes - (exists ? WindowBytes(key, previous!) : 0) + WindowBytes(key, window) > 65536) return false;
        _windows[key] = window; return !window.Ambiguous;
    }
    internal bool Reserve(string id, Dictionary<string, object?> payload, int bytes, DateTimeOffset now)
    {
        if (bytes < 0 || bytes > 65536 || _pending.ContainsKey(id)) return false;
        Prune(now); var key = Key(payload); var exists = _windows.TryGetValue(key, out var previous);
        if (!exists && _windows.Count >= 50) return false;
        var window = previous?.Copy() ?? new Window { Started = now };
        var at = window.FailureAt == default ? window.Started : window.FailureAt;
        if (window.Ambiguous || now < window.Started || now > at.AddMinutes(5) || window.Observations >= 20) return false;
        var kind = Text(payload, "kind"); var attempt = Text(payload, "attempt_id");
        if (kind == "recovery")
        {
            if (!window.Attempts.Contains(attempt) && window.Attempts.Count >= 5) return false;
            window.Attempts.Add(attempt);
        }
        else
        {
            if (window.Resource || window.FailureId != Text(payload, "source_event_id") || window.FailureAt == default || now > window.FailureAt.AddSeconds(30)) return false;
            window.Resource = true;
        }
        window.Observations++; window.Pending++;
        var cost = bytes + Encoding.UTF8.GetByteCount(id) + Encoding.UTF8.GetByteCount(key) + 64;
        if (Bytes - (exists ? WindowBytes(key, previous!) : 0) + WindowBytes(key, window) + cost > 65536) return false;
        _windows[key] = window; _pending[id] = new Receipt { Key = key, Kind = kind, Attempt = attempt, Bytes = bytes, Cost = cost }; _held += cost;
        return true;
    }
    internal bool Replace(string? original, string id, Dictionary<string, object?> payload, int bytes, DateTimeOffset now)
    {
        if (original != null && _pending.TryGetValue(original, out var receipt) && receipt.Key == Key(payload) && receipt.Kind == Text(payload, "kind") && receipt.Attempt == Text(payload, "attempt_id"))
        {
            var delta = bytes - receipt.Bytes + Encoding.UTF8.GetByteCount(id) - Encoding.UTF8.GetByteCount(original);
            if (bytes < 0 || bytes > 65536 || Bytes + delta > 65536 || id != original && _pending.ContainsKey(id) || !_windows.TryGetValue(receipt.Key, out var window) || window.Ambiguous || receipt.Kind != "recovery" && window.FailureId != Text(payload, "source_event_id")) { Release(original); return false; }
            _pending.Remove(original); receipt.Bytes = bytes; receipt.Cost += delta; _held += delta; _pending[id] = receipt; return true;
        }
        Release(original); return Reserve(id, payload, bytes, now);
    }
    internal void Release(string? id)
    {
        if (id == null || !_pending.TryGetValue(id, out var receipt)) return;
        _pending.Remove(id); _held -= receipt.Cost;
        if (_windows.TryGetValue(receipt.Key, out var window)) window.Pending = Math.Max(0, window.Pending - 1);
    }
    internal void Clear() { _pending.Clear(); _windows.Clear(); _held = 0; }
}
