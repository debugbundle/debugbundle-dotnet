using DebugBundle;
using DebugBundle.Transport;

namespace DebugBundle.Sdk.Tests;

public sealed class RecoveryTests
{
    [Fact]
    public async Task ExplicitPageScopeRequiresSessionAndValidOutcomeFields()
    {
        var transport = new FakeTransport();
        using var client = DebugBundleClient.Create(new DebugBundleOptions { ProjectMode = DebugBundleProjectMode.LocalOnly, Transport = transport, CaptureRecoveryObservations = true, EvidenceCapabilities = new EvidenceCapabilities { Features = new[] { "recovery_v1" } } });
        var page = new EvidenceScope { Kind = "page", PageId = Guid.NewGuid().ToString("D") }; var failure = Guid.NewGuid().ToString("D"); var attempt = Guid.NewGuid().ToString("D");
        var observation = new RecoveryObservation { FailureRef = failure, AttemptId = attempt, Scope = page, State = "recovery_observed", Operation = "fixture.check", Duration = TimeSpan.FromMilliseconds(12), StatusCode = 200 };
        client.CaptureRecovery(observation); Assert.Equal(0, client.PendingDiagnosticCount);
        client.SetContext("session_id", "0"); IDebugBundleClient universal = client; universal.CaptureRecovery(observation);
        observation.State = "inferred_success"; universal.CaptureRecovery(observation); observation.State = "attempt_completed"; observation.StatusCode = 600; universal.CaptureRecovery(observation); observation.StatusCode = 200; observation.Duration = TimeSpan.FromMilliseconds(-1); universal.CaptureRecovery(observation);
        await client.FlushAsync(); var captured = Assert.Single(Assert.Single(transport.Batches)); Assert.Equal("0", captured.Correlation!["session_id"]); Assert.Equal("fixture.check", captured.Payload["operation"]); Assert.Equal(12d, captured.Payload["duration_ms"]);
        Assert.Equal(0, client.PendingDiagnosticCount);
        var operation = new EvidenceScope { Kind = "operation", OperationId = Guid.NewGuid().ToString("D") }; var context = EvidenceContext.ForFailure(failure, operation); Assert.True(context.ContainsKey("evidence_scope")); Assert.Null(new EvidenceScope { Kind = "invalid", OperationId = operation.OperationId }.Wire());
    }

    [Fact]
    public async Task ExplicitRecoverySharesOwnedFailureScopeAndNeverInfersSuccessFromHttpStatus()
    {
        var transport = new FakeTransport();
        using var client = DebugBundleClient.Create(new DebugBundleOptions { ProjectMode = DebugBundleProjectMode.LocalOnly, Transport = transport, CaptureRecoveryObservations = true, EvidenceCapabilities = new EvidenceCapabilities { Features = new[] { "exception_evidence_v1", "recovery_v1" } } });
        var failure = Guid.NewGuid().ToString("D"); var attempt = Guid.NewGuid().ToString("D");
        await client.RunWithOperationContextAsync(new DebugBundleOperationOptions { Kind = "job" }, async () =>
        {
            client.CaptureException(new Exception("fixture"), EvidenceContext.ForFailure(failure));
            client.CaptureRecovery(new RecoveryObservation { FailureRef = failure, AttemptId = attempt, State = "attempt_completed", StatusCode = 200 });
            await Task.Yield();
            client.CaptureRecovery(new RecoveryObservation { FailureRef = failure, AttemptId = attempt, State = "recovery_observed" });
        });
        await client.FlushAsync(); var events = transport.Batches.SelectMany(batch => batch).ToArray(); Assert.Equal(3, events.Length);
        var reference = Assert.IsType<Dictionary<string, object?>>(events[0].Context!["evidence_reference"]);
        foreach (var observation in events.Skip(1))
        {
            Assert.True(BeforeSendProcessor.IsValid(observation)); Assert.Equal(reference["scope"], observation.Payload["scope"]);
        }
        Assert.Equal("attempt_completed", events[1].Payload["state"]); Assert.Equal("recovery_observed", events[2].Payload["state"]);
    }
    [Fact]
    public async Task OfflineRecoveryBudgetFollowsHookIdentityAndDoesNotRenewBeforeAcknowledgement()
    {
        var transport = new FakeTransport(); transport.EnqueueResponse(new EventTransportResult { StatusCode = 503, RetryAfter = TimeSpan.FromHours(1) });
        using var client = DebugBundleClient.Create(new DebugBundleOptions { ProjectMode = DebugBundleProjectMode.LocalOnly, Transport = transport, CaptureRecoveryObservations = true, BatchSize = 512, FlushInterval = TimeSpan.FromHours(1), EvidenceCapabilities = new EvidenceCapabilities { Features = new[] { "recovery_v1" } }, BeforeSend = candidate => { candidate.EventId = Guid.NewGuid().ToString("D"); return candidate; } });
        var failure = Guid.NewGuid().ToString("D"); var attempt = Guid.NewGuid().ToString("D");
        client.RunWithOperationContext(new DebugBundleOperationOptions { Kind = "command" }, () => { for (var index = 0; index < 100; index++) client.CaptureRecovery(new RecoveryObservation { FailureRef = failure, AttemptId = attempt, State = "attempt_started" }); });
        await client.FlushAsync(); Assert.Equal(20, Assert.Single(transport.Batches).Count);
        Assert.Equal(20, client.PendingDiagnosticCount); Assert.InRange(client.DiagnosticBytes, 1, 65536);
    }
}
