namespace DebugBundle;

public sealed class EvidenceScope
{
    public string Kind { get; set; } = "operation";
    public string? PageId { get; set; }
    public string? OperationId { get; set; }
    internal Dictionary<string, object?>? Wire()
    {
        var id = Kind == "page" ? PageId : Kind == "operation" ? OperationId : null;
        return EvidenceWire.FreshId(id) ? new Dictionary<string, object?> { ["kind"] = Kind, [Kind == "page" ? "page_id" : "operation_id"] = id } : null;
    }
}
public sealed class RecoveryObservation
{
    public string FailureRef { get; set; } = "";
    public string AttemptId { get; set; } = "";
    public string State { get; set; } = "";
    public EvidenceScope? Scope { get; set; }
    public string? Operation { get; set; }
    public int? StatusCode { get; set; }
    public TimeSpan? Duration { get; set; }
}
public static class EvidenceContext
{
    public static Dictionary<string, object?> ForFailure(string failureRef, EvidenceScope? scope = null)
    {
        var context = new Dictionary<string, object?> { ["failure_ref"] = failureRef };
        if (scope?.Wire() is { } wire) context["evidence_scope"] = wire;
        return context;
    }
}
// Optional capability keeps existing implementations of the base client compatible.
public interface IDebugBundleEvidenceClient : IDebugBundleOperationClient
{
    void CaptureRecovery(RecoveryObservation? observation);
}
public static class DebugBundleEvidenceExtensions
{
    public static void CaptureRecovery(this IDebugBundleClient? client, RecoveryObservation? observation) => (client as IDebugBundleEvidenceClient)?.CaptureRecovery(observation);
}
public static partial class DebugBundle
{
    public static void CaptureRecovery(RecoveryObservation? observation) => Current?.CaptureRecovery(observation);
}
