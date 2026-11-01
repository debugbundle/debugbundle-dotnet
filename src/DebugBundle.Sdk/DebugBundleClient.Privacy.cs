using System.Text.Json;
using DebugBundle.Redaction;

namespace DebugBundle;

public sealed partial class DebugBundleClient
{
    // Revalidate after mandatory privacy. Optional metadata may be reduced, but
    // the latest accepted primary is never replaced with its pre-hook contents.
    private DebugBundleEventEnvelope? ProtectCandidate(DebugBundleEventEnvelope envelope)
    {
        var safe = TelemetryPrivacy.ProtectEvent(envelope, _options.RedactFields);
        if (safe != null && BeforeSendProcessor.IsValid(safe)) return FilterEvidence(safe);
        foreach (var mark in new[] { true, false })
        {
            var reduced = WithoutOptionalEvidence(envelope, mark);
            if (reduced == null) continue;
            safe = TelemetryPrivacy.ProtectEvent(reduced, _options.RedactFields);
            if (safe != null && BeforeSendProcessor.IsValid(safe)) return FilterEvidence(safe);
        }
        return null;
    }
    // Configuration controls policy for custom/file senders, but cannot qualify
    // an unrelated delivery/reader hop. Explicit complete-path pins remain available.
    private void ObserveRemoteEvidence(System.Text.Json.JsonElement? descriptor)
    {
        if (_transport is Transport.HttpEventTransport native && native.MatchesEndpoint(_options.Endpoint)) _evidence.Observe(descriptor);
    }
    private DebugBundleEventEnvelope? FilterEvidence(DebugBundleEventEnvelope envelope)
    {
        bool minimal;
        lock (_sync) minimal = _remoteConfig.CapturePolicy.Preset == "minimal" || !_remoteConfig.CapturePolicy.CaptureDiagnosticContext;
        if (envelope.EventType == "diagnostic_context" && (minimal || (envelope.Payload["kind"] as string == "recovery" ? !_options.CaptureRecoveryObservations || !_evidence.Supports("recovery_v1") : !_evidence.Supports("resource_observations_v1")))) return null;
        if (!_options.CaptureExceptionEvidence || !_evidence.Supports("exception_evidence_v1"))
        { envelope.Payload.Remove("exception_evidence"); envelope.Payload.Remove("execution"); }
        if (minimal || !_evidence.Supports("resource_observations_v1")) envelope.Payload.Remove("resource_observations");
        if ((minimal || !_evidence.Supports("recovery_v1") && !_evidence.Supports("resource_observations_v1")) &&
            envelope.Context?.TryGetValue("evidence_reference", out var reference) == true)
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(reference, OwnedJson.Options));
            if (EvidenceWire.Reference(document.RootElement)) envelope.Context.Remove("evidence_reference");
        }
        return envelope;
    }
    internal static DebugBundleEventEnvelope? WithoutOptionalEvidence(DebugBundleEventEnvelope envelope, bool mark)
    {
        if (envelope.EventType is not ("backend_exception" or "frontend_exception")) return null;
        try
        {
            var owned = OwnedJson.Snapshot(envelope);
            var payload = (Dictionary<string, object?>)owned["payload"]!;
            var limits = new Dictionary<string, object?> { ["version"] = 1 };
            foreach (var field in new[] { "exception_evidence", "execution", "resource_observations" })
                if (payload.Remove(field)) limits[field] = "budget_exceeded";
            if (limits.Count == 1) return null;
            var context = owned.TryGetValue("context", out var value) ? (Dictionary<string, object?>)value! : new Dictionary<string, object?>();
            if (context.TryGetValue("evidence_reference", out var reference))
            {
                using var document = JsonDocument.Parse(JsonSerializer.Serialize(reference, OwnedJson.Options));
                if (EvidenceWire.Reference(document.RootElement)) context.Remove("evidence_reference");
            }
            if (mark) context["evidence_limits"] = limits;
            owned["context"] = context;
            return OwnedJson.Event(owned);
        }
        catch { return null; }
    }
}
