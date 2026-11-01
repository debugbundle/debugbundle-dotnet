using System.Text.Json;
using DebugBundle.Transport;

namespace DebugBundle;

public sealed partial class DebugBundleClient
{
    // Retries and unsent tails retain the accepted protected identity. Application
    // hooks, projections and sampling run once; final policy/proof still reapply.
    private DebugBundleEventEnvelope? PrepareQueuedEvent(DebugBundleEventEnvelope captured)
    {
        DebugBundleEventEnvelope? safe;
        if (captured.Prepared) safe = ProtectCandidate(captured);
        else
        {
            var firstPreparation = captured.PendingSampling;
            var sampled = true;
            if (captured.PendingSampling)
            {
                captured.PendingSampling = false;
                try { sampled = _options.RandomSource() <= _options.SampleRate; } catch { sampled = false; }
            }
            if (!sampled) return null;
            ExceptionProjection.Enrich(captured, _options.CaptureExceptionEvidence && _evidence.Supports("exception_evidence_v1"), captured.Fatal);
            var projection = captured.PendingProjection;
            captured.PendingProjection = null;
            try { if (!captured.Fatal) projection?.Invoke(captured); } catch { /* Preserve the safe capture snapshot. */ }
            var projected = ProtectCandidate(captured);
            if (projected != null && firstPreparation && projected.EventType is not ("probe_event" or "diagnostic_context"))
            {
                var occurredAt = DateTimeOffset.TryParse(projected.OccurredAt, out var capturedAt) ? capturedAt : DateTimeOffset.UtcNow;
                if (!_suppression.ShouldCapture(SuppressionTracker.Fingerprint(projected.EventType, projected.Payload), occurredAt)) return null;
            }
            var prepared = projected == null || captured.Fatal ? projected : BeforeSendProcessor.Apply(projected, _options.BeforeSend);
            safe = prepared == null ? null : ProtectCandidate(prepared);
        }
        if (safe == null || !ShouldCapturePreparedEvent(safe)) return null;
        if (captured.Fatal) FitFatalEvidence(safe);
        safe = FitNativeEvent(safe);
        if (safe != null) { safe.Prepared = true; safe.Fatal = captured.Fatal; }
        return safe;
    }
    private static void FitFatalEvidence(DebugBundleEventEnvelope envelope)
    {
        if (!envelope.Payload.TryGetValue("exception_evidence", out var value) || value is not Dictionary<string, object?> graph) return;
        var additions = new Dictionary<string, object?> { ["exception_evidence"] = graph };
        if (envelope.Payload.TryGetValue("execution", out var execution)) additions["execution"] = execution;
        if (envelope.Context?.TryGetValue("evidence_reference", out var reference) == true)
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(reference, OwnedJson.Options));
            if (EvidenceWire.Reference(document.RootElement)) additions["evidence_reference"] = reference;
        }
        var size = AcceptingJsonSize.Size(additions);
        if (size > 4096) ExceptionEvidence.Fit(graph, Math.Max(512, AcceptingJsonSize.Size(graph) - (size - 4096)));
    }
    private static int NativeEventBytes(DebugBundleEventEnvelope envelope) => JsonSerializer.SerializeToUtf8Bytes(envelope, JsonSerialization.Options).Length;
    private DebugBundleEventEnvelope? FitNativeEvent(DebugBundleEventEnvelope envelope)
    {
        if (_transport is not HttpEventTransport || NativeEventBytes(envelope) <= BoundedHttpResponse.MaxBytes - 13) return envelope;
        foreach (var mark in new[] { true, false })
        {
            var reduced = WithoutOptionalEvidence(envelope, mark);
            var safe = reduced == null ? null : ProtectCandidate(reduced);
            if (safe != null && NativeEventBytes(safe) <= BoundedHttpResponse.MaxBytes - 13) return safe;
        }
        return null;
    }
    private void TakeNativePrefix(List<DebugBundleEventEnvelope> batch, Dictionary<DebugBundleEventEnvelope, int> sizes)
    {
        if (_transport is not HttpEventTransport) return;
        var bytes = 13; var count = 0;
        foreach (var envelope in batch)
        {
            var next = NativeEventBytes(envelope) + (count > 0 ? 1 : 0);
            if (bytes + next > BoundedHttpResponse.MaxBytes) break;
            bytes += next; count++;
        }
        if (count == batch.Count) return;
        lock (_sync)
        {
            var tails = batch.Skip(count).ToArray();
            foreach (var envelope in tails) ReleasePreparedOriginalLocked(envelope, sizes[envelope]);
            RestoreBatchLocked(tails, sizes);
            foreach (var envelope in tails) sizes.Remove(envelope);
            batch.RemoveRange(count, batch.Count - count);
            if (count > 0 && !_disposed) RequestFlushLocked();
        }
    }
    private DebugBundleEventEnvelope? DowngradeRejectedEvidence(DebugBundleEventEnvelope original)
    {
        if (original.EventType is not ("backend_exception" or "frontend_exception")) return null;
        var before = JsonSerializer.Serialize(original, JsonSerialization.Options);
        var reduced = ProtectCandidate(original);
        if (reduced == null || JsonSerializer.Serialize(reduced, JsonSerialization.Options) == before) return null;
        reduced = FitNativeEvent(reduced);
        if (reduced != null) reduced.Prepared = true;
        return reduced;
    }
}
