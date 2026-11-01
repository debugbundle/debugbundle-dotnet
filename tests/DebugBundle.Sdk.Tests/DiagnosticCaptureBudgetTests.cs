using DebugBundle;

namespace DebugBundle.Sdk.Tests;

public sealed class DiagnosticCaptureBudgetTests
{
    private static Dictionary<string, object?> Payload(string? failure = null, string? attempt = null) => new()
    {
        ["kind"] = "recovery",
        ["failure_ref"] = failure ?? Guid.NewGuid().ToString("D"),
        ["attempt_id"] = attempt ?? Guid.NewGuid().ToString("D"),
        ["scope"] = new Dictionary<string, object?> { ["kind"] = "operation", ["operation_id"] = "12345678-1234-4567-8123-123456789abc" }
    };
    [Fact]
    public void AcknowledgedObservationsKeepTheirAllowanceUntilWindowExpiry()
    {
        var budget = new DiagnosticCaptureBudget(); var at = DateTimeOffset.UtcNow; var payload = Payload();
        for (var index = 0; index < 20; index++) { var id = Guid.NewGuid().ToString("D"); Assert.True(budget.Reserve(id, payload, 100, at)); budget.Release(id); }
        Assert.False(budget.Reserve("extra", payload, 100, at.AddMinutes(4)));
        Assert.True(budget.Reserve("renewed", payload, 100, at.AddMinutes(6)));
    }
    [Fact]
    public void PendingOfflineReceiptPreventsRenewalAndIdentityReplacementKeepsOneReceipt()
    {
        var budget = new DiagnosticCaptureBudget(); var at = DateTimeOffset.UtcNow; var payload = Payload();
        Assert.True(budget.Reserve("old", payload, 100, at));
        Assert.True(budget.Replace("old", "new", payload, 200, at.AddMinutes(6)));
        Assert.Equal(1, budget.PendingCount);
        Assert.False(budget.Reserve("late", payload, 100, at.AddMinutes(6)));
        budget.Release("old"); Assert.Equal(1, budget.PendingCount);
        budget.Release("new"); Assert.Equal(0, budget.PendingCount);
        Assert.True(budget.Reserve("renewed", payload, 100, at.AddMinutes(6)));
    }
    [Fact]
    public void AttemptWindowAndSerializedOwnershipBudgetsAreIndependent()
    {
        var budget = new DiagnosticCaptureBudget(); var at = DateTimeOffset.UtcNow; var payload = Payload();
        for (var index = 0; index < 5; index++) { payload["attempt_id"] = Guid.NewGuid().ToString("D"); Assert.True(budget.Reserve("attempt" + index, payload, 100, at)); }
        payload["attempt_id"] = Guid.NewGuid().ToString("D"); Assert.False(budget.Reserve("sixth", payload, 100, at));
        Assert.False(budget.Reserve("oversize", Payload(), 65536, at)); Assert.InRange(budget.Bytes, 1, 65536);
        budget.Clear(); Assert.Equal(0, budget.Bytes); Assert.Equal(0, budget.PendingCount);
        for (var index = 0; index < 50; index++) { var item = Payload(); Assert.True(budget.Register(item, "event" + index, at)); }
        Assert.False(budget.Register(Payload(), "fifty-first", at));
    }
    [Fact]
    public void LateResourceRequiresOneUnambiguousRetainedPrimaryWithinThirtySeconds()
    {
        var budget = new DiagnosticCaptureBudget(); var at = DateTimeOffset.UtcNow; var payload = Payload(); payload["kind"] = "resource_observation"; payload.Remove("attempt_id"); payload["source_event_id"] = "primary";
        Assert.False(budget.Reserve("early", payload, 100, at)); Assert.True(budget.Register(payload, "primary", at));
        Assert.False(budget.Reserve("expired", payload, 100, at.AddSeconds(31)));
        Assert.True(budget.Reserve("one", payload, 100, at.AddSeconds(29))); budget.Release("one"); Assert.False(budget.Reserve("two", payload, 100, at.AddSeconds(29)));
        Assert.False(budget.Register(payload, "other-primary", at)); Assert.False(budget.Replace(null, "ambiguous", payload, 100, at));
    }
    [Fact]
    public void HookScopeChangesCannotReuseOrStealAnExistingReceipt()
    {
        var budget = new DiagnosticCaptureBudget(); var at = DateTimeOffset.UtcNow; var payload = Payload();
        Assert.True(budget.Reserve("first", payload, 100, at)); Assert.True(budget.Reserve("collision", payload, 100, at));
        Assert.False(budget.Replace("first", "collision", payload, 100, at)); Assert.Equal(1, budget.PendingCount);
        payload["failure_ref"] = Guid.NewGuid().ToString("D"); Assert.True(budget.Replace("collision", "latest", payload, 120, at)); Assert.Equal(1, budget.PendingCount);
        Assert.False(budget.Replace("latest", "oversized", payload, 65536, at)); Assert.Equal(0, budget.PendingCount);
    }
}
