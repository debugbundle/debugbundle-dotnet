namespace DebugBundle;

internal static class BeforeSendProcessor
{
    public static DebugBundleEventEnvelope? Apply(DebugBundleEventEnvelope envelope,
        Func<DebugBundleEventEnvelope, DebugBundleEventEnvelope?>? hook)
    {
        if (hook == null) return envelope;
        DebugBundleEventEnvelope input;
        try { input = OwnedJson.Event(OwnedJson.Snapshot(envelope)); }
        catch { return envelope; }
        DebugBundleEventEnvelope? result;
        try { result = hook(input); }
        catch { return envelope; }
        if (result == null) return null;
        try
        {
            var owned = OwnedJson.Snapshot(result);
            return EvidenceWire.Accepts(owned) ? OwnedJson.Event(owned) : envelope;
        }
        catch (EvidenceValidationUnavailableException) { return null; }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { return null; }
        catch { return envelope; }
    }
    internal static bool IsValid(DebugBundleEventEnvelope envelope)
    {
        try { return EvidenceWire.Accepts(OwnedJson.Snapshot(envelope)); }
        catch { return false; }
    }
}
