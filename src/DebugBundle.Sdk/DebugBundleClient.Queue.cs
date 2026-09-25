using System.Text.Json;

namespace DebugBundle;

public sealed partial class DebugBundleClient
{
    private const int MaxQueuedEvents = 512;
    private const int MaxOwnedEvents = 1_000;
    private const int MaxOwnedBytes = 8 * 1024 * 1024;
    private const int MaxOrdinaryEvents = 800;
    // Ownership follows the retained object, since valid hooks may replace event IDs.
    private readonly Dictionary<DebugBundleEventEnvelope, int> _queuedSizes = new();
    private readonly int[] _queuedPriorities = new int[4];
    private readonly Dictionary<string, PressureCount> _pressureDrops = new(StringComparer.Ordinal);
    private int _queuedBytes;
    private int _inFlightEvents;
    private int _inFlightBytes;
    private int _inFlightOrdinary;
    private long _contentionDrops;

    private bool MightAdmit(int priority, string kind)
    {
        if (!Monitor.TryEnter(_sync))
        {
            Interlocked.Increment(ref _contentionDrops);
            return false;
        }
        try
        {
            if (_disposed || !_options.Enabled || _transport == null) return false;
            if (_buffer.Count < MaxQueuedEvents &&
                _buffer.Count + _inFlightEvents < MaxOwnedEvents &&
                _queuedBytes + _inFlightBytes < MaxOwnedBytes &&
                (priority >= 2 || QueuedOrdinaryLocked() + _inFlightOrdinary < MaxOrdinaryEvents)) return true;
            if (HasLowerPriorityLocked(priority)) return true;
            RecordPressureKindLocked(kind);
            return false;
        }
        finally { Monitor.Exit(_sync); }
    }

    private static int EventBytes(DebugBundleEventEnvelope envelope) => JsonSerializer.SerializeToUtf8Bytes(envelope).Length;

    private static int EventPriorityFromPayload(IReadOnlyDictionary<string, object?> payload)
    {
        var level = ParseLogLevel(payload.TryGetValue("level", out var value) ? value : null);
        return level >= DebugBundleLogLevel.Error ? 2 : 0;
    }

    private static int RequestPriorityFromPayload(IReadOnlyDictionary<string, object?> payload) =>
        payload.TryGetValue("response_status", out var status) && IsFailedRequestStatus(status) ? 2 : 1;

    private static bool IsFailedRequestStatus(object? value)
    {
        if (value is int status) return status >= 400;
        if (value is long longStatus) return longStatus >= 400;
        return value is JsonElement element && element.ValueKind == JsonValueKind.Number &&
            element.TryGetInt32(out var jsonStatus) && jsonStatus >= 400;
    }

    private bool AdmitLocked(DebugBundleEventEnvelope envelope, int bytes)
    {
        if (bytes > MaxOwnedBytes)
        {
            RecordPressureLocked(envelope);
            return false;
        }

        var priority = EventPriority(envelope);
        while (_buffer.Count >= MaxQueuedEvents ||
               _buffer.Count + _inFlightEvents >= MaxOwnedEvents ||
               _queuedBytes + _inFlightBytes + bytes > MaxOwnedBytes ||
               (priority < 2 && QueuedOrdinaryLocked() + _inFlightOrdinary >= MaxOrdinaryEvents))
        {
            if (!HasLowerPriorityLocked(priority))
            {
                RecordPressureLocked(envelope);
                return false;
            }
            var index = _buffer.FindIndex(candidate => EventPriority(candidate) < priority);
            if (index < 0)
            {
                RecordPressureLocked(envelope);
                return false;
            }
            var evicted = _buffer[index];
            _buffer.RemoveAt(index);
            RemoveOwnershipLocked(evicted);
            RecordPressureLocked(evicted);
        }

        _buffer.Add(envelope);
        _queuedSizes[envelope] = bytes;
        _queuedBytes += bytes;
        _queuedPriorities[priority]++;
        return true;
    }

    private void RemoveOwnershipLocked(DebugBundleEventEnvelope envelope)
    {
        _queuedBytes -= _queuedSizes[envelope];
        _queuedSizes.Remove(envelope);
        _queuedPriorities[EventPriority(envelope)]--;
    }

    private bool HasLowerPriorityLocked(int priority)
    {
        for (var index = 0; index < priority; index++)
            if (_queuedPriorities[index] > 0) return true;
        return false;
    }

    private int QueuedOrdinaryLocked() => _queuedPriorities[0] + _queuedPriorities[1];

    private void MoveQueueToInFlightLocked()
    {
        _inFlightEvents = _buffer.Count;
        _inFlightBytes = _queuedBytes;
        _inFlightOrdinary = QueuedOrdinaryLocked();
        ClearQueueLocked();
    }

    private void ReleaseInFlightLocked()
    {
        _inFlightEvents = 0;
        _inFlightBytes = 0;
        _inFlightOrdinary = 0;
    }

    private void ReleasePreparedOriginalLocked(DebugBundleEventEnvelope envelope, int bytes)
    {
        _inFlightEvents--;
        _inFlightBytes -= bytes;
        if (EventPriority(envelope) < 2) _inFlightOrdinary--;
    }

    // Hooks can enlarge or reprioritize an event. Charge the final safe envelope before
    // retaining it for transport; only unsent lower-priority work may be displaced.
    private bool ReservePreparedLocked(DebugBundleEventEnvelope envelope, int bytes)
    {
        var priority = EventPriority(envelope);
        if (bytes > MaxOwnedBytes) return RejectPreparedLocked(envelope);
        while (_buffer.Count + _inFlightEvents >= MaxOwnedEvents ||
               _queuedBytes + _inFlightBytes + bytes > MaxOwnedBytes ||
               (priority < 2 && QueuedOrdinaryLocked() + _inFlightOrdinary >= MaxOrdinaryEvents))
        {
            var index = _buffer.FindIndex(candidate => EventPriority(candidate) < priority);
            if (index < 0) return RejectPreparedLocked(envelope);
            var evicted = _buffer[index];
            _buffer.RemoveAt(index);
            RemoveOwnershipLocked(evicted);
            RecordPressureLocked(evicted);
        }
        _inFlightEvents++;
        _inFlightBytes += bytes;
        if (priority < 2) _inFlightOrdinary++;
        return true;
    }

    private bool RejectPreparedLocked(DebugBundleEventEnvelope envelope)
    {
        // A summary that cannot fit must not generate another summary indefinitely.
        if (envelope.EventType != "error_suppressed") RecordPressureLocked(envelope);
        return false;
    }

    private void AddPreparedAggregate(List<DebugBundleEventEnvelope> batch,
        Dictionary<DebugBundleEventEnvelope, int> sizes, DebugBundleEventEnvelope envelope)
    {
        var bytes = EventBytes(envelope);
        lock (_sync)
        {
            if (!ReservePreparedLocked(envelope, bytes)) return;
            batch.Add(envelope);
            sizes[envelope] = bytes;
        }
    }

    private static int EventPriority(DebugBundleEventEnvelope envelope)
    {
        if (envelope.EventType == "backend_exception") return 3;
        if (envelope.EventType == "error_suppressed") return 2;
        if (envelope.EventType == "request_event") return RequestPriorityFromPayload(envelope.Payload);
        if (envelope.EventType != "log_event") return 1;
        var level = ParseLogLevel(envelope.Payload.TryGetValue("level", out var value) ? value : null);
        return level >= DebugBundleLogLevel.Error ? 2 : 0;
    }

    private void RecordPressureLocked(DebugBundleEventEnvelope envelope)
    {
        var kind = envelope.EventType == "log_event"
            ? LevelName(ParseLogLevel(envelope.Payload.TryGetValue("level", out var level) ? level : null))
            : envelope.EventType == "backend_exception" ? "exception" : "other";
        RecordPressureKindLocked(kind);
    }

    private void RecordPressureKindLocked(string kind)
    {
        if (!_pressureDrops.TryGetValue(kind, out var count))
            _pressureDrops[kind] = count = new PressureCount { First = DateTimeOffset.UtcNow };
        count.Count++;
        count.Last = DateTimeOffset.UtcNow;
    }

    private Dictionary<string, PressureCount> DrainPressureLocked()
    {
        var contention = Interlocked.Exchange(ref _contentionDrops, 0);
        if (contention > 0)
            _pressureDrops["contended"] = new PressureCount
            {
                Count = contention,
                First = DateTimeOffset.UtcNow,
                Last = DateTimeOffset.UtcNow
            };
        var pending = new Dictionary<string, PressureCount>(_pressureDrops, StringComparer.Ordinal);
        _pressureDrops.Clear();
        return pending;
    }

    private sealed class PressureCount
    {
        public long Count { get; set; }
        public DateTimeOffset First { get; set; }
        public DateTimeOffset Last { get; set; }
    }

    private void ClearQueueLocked()
    {
        _buffer.Clear();
        _queuedSizes.Clear();
        _queuedBytes = 0;
        Array.Clear(_queuedPriorities, 0, _queuedPriorities.Length);
    }

    private void RestoreBatchLocked(IEnumerable<DebugBundleEventEnvelope> batch, IReadOnlyDictionary<DebugBundleEventEnvelope, int> sizes)
    {
        foreach (var envelope in batch.OrderByDescending(EventPriority))
            if (sizes.TryGetValue(envelope, out var bytes)) AdmitLocked(envelope, bytes);
    }
}
