using DebugBundle;

namespace DebugBundle.Sdk.Tests;

public sealed class OperationContextTests
{
    private sealed class HostContext : Dictionary<string, object?>, IDictionary<string, object?>
    {
        internal int Reads;
        bool IDictionary<string, object?>.ContainsKey(string key) { Reads++; throw new InvalidOperationException("host accessor"); }
    }

    [Fact]
    public async Task IdentityClassificationDoesNotInvokeApplicationDictionaryMethods()
    {
        var transport = new FakeTransport();
        using var client = DebugBundleClient.Create(new DebugBundleOptions { ProjectMode = DebugBundleProjectMode.LocalOnly, Transport = transport, BatchSize = 1000 });
        var context = new HostContext();
        client.CaptureException(new InvalidOperationException("native failure"), context);
        await client.FlushAsync();
        Assert.Equal(0, context.Reads);
        Assert.Single(transport.Batches.SelectMany(batch => batch));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReusedNativeObjectAndRejectedHookDoNotHideLaterFailures(bool dropFirst)
    {
        var transport = new FakeTransport();
        var calls = 0;
        using var client = DebugBundleClient.Create(new DebugBundleOptions
        {
            ProjectMode = DebugBundleProjectMode.LocalOnly,
            Transport = transport,
            BatchSize = 1000,
            BeforeSend = dropFirst ? envelope => calls++ == 0 ? null : envelope : null
        });
        using var operation = client.BeginOperation(new DebugBundleOperationOptions { Kind = "request" });
        var error = new InvalidOperationException("reused object");
        client.CaptureException(error, new Dictionary<string, object?> { ["logger"] = "synthetic" });
        await client.FlushAsync();
        client.CaptureException(error, dropFirst ? null : new Dictionary<string, object?> { ["logger"] = "synthetic" });
        await client.FlushAsync();
        Assert.Equal(dropFirst ? 1 : 2, transport.Batches.SelectMany(batch => batch).Count());
    }

    [Fact]
    public async Task LoggerAndMiddlewareShareOneNativeOccurrenceWithinEachOperation()
    {
        var transport = new FakeTransport();
        using var client = DebugBundleClient.Create(new DebugBundleOptions { ProjectMode = DebugBundleProjectMode.LocalOnly, Transport = transport, BatchSize = 1000 });
        var error = new InvalidOperationException("same native object");
        for (var index = 0; index < 2; index++)
        {
            using var operation = client.BeginOperation(new DebugBundleOperationOptions { Kind = "request" });
            client.CaptureException(error, new Dictionary<string, object?> { ["logger"] = "synthetic" });
            client.CaptureException(error);
        }
        await client.FlushAsync();
        Assert.Equal(2, transport.Batches.SelectMany(batch => batch).Count());
    }

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
