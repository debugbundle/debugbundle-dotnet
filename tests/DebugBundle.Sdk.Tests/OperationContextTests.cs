using DebugBundle;

namespace DebugBundle.Sdk.Tests;

public sealed class OperationContextTests
{
    [Fact]
    public async Task AsyncOperationPreservesResultsAndRevokesCopiedContextAfterCompletion()
    {
        var transport = new FakeTransport();
        using var client = DebugBundleClient.Create(new DebugBundleOptions { ProjectMode = DebugBundleProjectMode.LocalOnly, Transport = transport, EvidenceCapabilities = new EvidenceCapabilities { Features = new[] { "exception_evidence_v1" } } });
        Func<Task>? late = null;
        var result = await client.RunWithOperationContextAsync(new DebugBundleOperationOptions { Kind = "job", Name = "queue.process", Limit = TimeSpan.FromSeconds(10) }, async () =>
        {
            await Task.Yield();
            client.CaptureException(new InvalidOperationException("owned operation"));
            var execution = ExecutionContext.Capture();
            late = () => { ExecutionContext.Run(execution!, _ => client.CaptureException(new Exception("late operation")), null); return Task.CompletedTask; };
            return 42;
        });
        Assert.Equal(42, result);
        await late!(); await client.FlushAsync();
        var events = transport.Batches.SelectMany(batch => batch).ToArray();
        Assert.Equal(2, events.Length);
        var execution = Assert.IsType<Dictionary<string, object?>>(events[0].Payload["execution"]);
        Assert.Equal("job", execution["kind"]); Assert.Equal("queue.process", execution["name"]);
        Assert.Equal(10000d, Convert.ToDouble(execution["limit_ms"]));
        Assert.DoesNotContain("execution", events[1].Payload.Keys);
    }
    [Fact]
    public void OperationCallbacksRetainExactExceptionAndNestedCompletionCannotRestoreDeadParent()
    {
        using var client = DebugBundleClient.Create(new DebugBundleOptions { ProjectMode = DebugBundleProjectMode.LocalOnly, Transport = new FakeTransport(), EvidenceCapabilities = new EvidenceCapabilities { Features = new[] { "exception_evidence_v1" } } });
        var error = new InvalidOperationException("exact callback exception");
        Assert.Same(error, Assert.Throws<InvalidOperationException>(() => client.RunWithOperationContext(new DebugBundleOperationOptions(), () => throw error)));
        using var outer = client.BeginOperation(new DebugBundleOperationOptions { Kind = "command" });
        using var inner = client.BeginOperation(new DebugBundleOperationOptions { Kind = "job" });
        Assert.NotNull(client.CurrentExecution());
        outer.Dispose();
        Assert.Null(client.CurrentExecution());
        inner.Dispose();
        Assert.Null(client.CurrentExecution());
    }
}
