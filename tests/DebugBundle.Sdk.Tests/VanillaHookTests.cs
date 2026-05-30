using DebugBundle;

namespace DebugBundle.Sdk.Tests;

public sealed class VanillaHookTests
{
    [Fact]
    public async Task WithExceptionCapture_Captures_And_Rethrows()
    {
        var transport = new FakeTransport();
        global::DebugBundle.DebugBundle.Init(new DebugBundleOptions
        {
            ProjectToken = "dbundle_proj_test",
            Service = "checkout-worker",
            Environment = "test",
            Transport = transport,
            RandomSource = () => 0
        });

        var thrown = Assert.Throws<InvalidOperationException>(() =>
            global::DebugBundle.DebugBundle.WithExceptionCapture(() => throw new InvalidOperationException("wrapped failure"), new Dictionary<string, object?> { ["job_id"] = "job_123" }));

        Assert.Equal("wrapped failure", thrown.Message);
        await global::DebugBundle.DebugBundle.FlushAsync();
        var captured = transport.Batches.Single().Single(item => item.EventType == "backend_exception");
        var context = Assert.IsType<Dictionary<string, object?>>(captured.Payload["context"]);
        Assert.Equal("job_123", context["job_id"]);
    }

    [Fact]
    public async Task WithExceptionCaptureAsync_Captures_And_Rethrows()
    {
        var transport = new FakeTransport();
        global::DebugBundle.DebugBundle.Init(new DebugBundleOptions
        {
            ProjectToken = "dbundle_proj_test",
            Service = "checkout-worker",
            Environment = "test",
            Transport = transport,
            RandomSource = () => 0
        });

        await Assert.ThrowsAsync<ApplicationException>(() =>
            global::DebugBundle.DebugBundle.WithExceptionCaptureAsync(() => throw new ApplicationException("async wrapped failure")));

        await global::DebugBundle.DebugBundle.FlushAsync();
        Assert.Contains(transport.Batches.Single(), item => item.EventType == "backend_exception");
    }
}
