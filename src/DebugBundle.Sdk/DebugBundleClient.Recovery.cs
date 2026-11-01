using System.Text.Json;

namespace DebugBundle;

public sealed partial class DebugBundleClient
{
    private readonly DiagnosticCaptureBudget _diagnostics = new();
    internal int PendingDiagnosticCount { get { lock (_sync) return _diagnostics.PendingCount; } }
    internal int DiagnosticBytes { get { lock (_sync) return _diagnostics.Bytes; } }
    private Dictionary<string, object?>? RecoveryScope(EvidenceScope? explicitScope = null)
    {
        if (explicitScope != null) return explicitScope.Wire();
        var owner = OperationContext.Current;
        return owner?.Active(_operations) == true ? new Dictionary<string, object?> { ["kind"] = "operation", ["operation_id"] = owner.Id } : null;
    }
    public void CaptureRecovery(RecoveryObservation? observation)
    {
        try
        {
            if (observation == null || !_options.CaptureRecoveryObservations || !_evidence.Supports("recovery_v1") || !EvidenceWire.FreshId(observation.FailureRef) || !EvidenceWire.FreshId(observation.AttemptId)) return;
            var scope = RecoveryScope(observation.Scope); if (scope == null) return;
            var payload = new Dictionary<string, object?> { ["version"] = 1, ["kind"] = "recovery", ["failure_ref"] = observation.FailureRef, ["scope"] = scope, ["attempt_id"] = observation.AttemptId, ["state"] = observation.State };
            if (observation.Operation != null) payload["operation"] = observation.Operation;
            if (observation.StatusCode != null) payload["status_code"] = observation.StatusCode.Value;
            if (observation.Duration != null) payload["duration_ms"] = observation.Duration.Value.TotalMilliseconds;
            Capture("diagnostic_context", payload);
        }
        catch { /* Optional observations must never affect the application. */ }
    }
    private void AddFailureReference(Dictionary<string, object?> context)
    {
        if (!_options.CaptureRecoveryObservations || !_evidence.Supports("recovery_v1") || !context.TryGetValue("failure_ref", out var failure) || !EvidenceWire.FreshId(failure as string)) return;
        var scope = context.TryGetValue("evidence_scope", out var supplied) && supplied is Dictionary<string, object?> map ? map : RecoveryScope();
        if (scope == null) return;
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(scope, OwnedJson.Options));
        if (EvidenceWire.Scope(document.RootElement)) context["evidence_reference"] = new Dictionary<string, object?> { ["version"] = 1, ["failure_ref"] = failure, ["scope"] = scope };
    }
    private void ReleaseDiagnosticLocked(DebugBundleEventEnvelope envelope)
    { _diagnostics.Release(envelope.DiagnosticId); envelope.DiagnosticId = null; }
    private bool ReconcileDiagnosticLocked(DebugBundleEventEnvelope captured, DebugBundleEventEnvelope safe, int bytes)
    {
        if (safe.EventType == "diagnostic_context")
        {
            if (!_diagnostics.Replace(captured.DiagnosticId, safe.EventId, safe.Payload, bytes, DateTimeOffset.UtcNow)) { captured.DiagnosticId = null; return false; }
            captured.DiagnosticId = null; safe.DiagnosticId = safe.EventId;
        }
        else ReleaseDiagnosticLocked(captured);
        return true;
    }
    private void RegisterPrimaryLocked(DebugBundleEventEnvelope safe)
    {
        if (safe.EventType is "backend_exception" or "frontend_exception" && safe.Context?.TryGetValue("evidence_reference", out var value) == true)
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(value, OwnedJson.Options));
            if (EvidenceWire.Reference(document.RootElement)) _diagnostics.Register((Dictionary<string, object?>)value!, safe.EventId, DateTimeOffset.UtcNow);
        }
    }
}
