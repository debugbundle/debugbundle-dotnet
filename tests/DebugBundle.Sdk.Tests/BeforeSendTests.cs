using DebugBundle;

namespace DebugBundle.Sdk.Tests;

public sealed class BeforeSendTests
{
    [Fact]
    public async Task Hook_Runs_After_Redaction_And_Mutates_Before_Queueing()
    {
        var transport = new FakeTransport();
        object? observedPassword = null;
        var client = CreateClient(transport, envelope =>
        {
            var attributes = Assert.IsType<Dictionary<string, object?>>(envelope.Payload["attributes"]);
            observedPassword = attributes["password"];
            envelope.Payload["message"] = "mutated";
            return envelope;
        });

        client.CaptureMessage(
            "original",
            DebugBundleLogLevel.Error,
            new Dictionary<string, object?> { ["password"] = "secret" });
        await client.FlushAsync();

        Assert.Equal("[REDACTED]", observedPassword);
        Assert.Equal("mutated", transport.Batches.Single().Single().Payload["message"]);
    }

    [Fact]
    public async Task Hook_Drop_Invalid_Failure_And_Sampling_Are_Safe()
    {
        var dropTransport = new FakeTransport();
        var calls = 0;
        var droppingClient = CreateClient(dropTransport, envelope =>
        {
            calls++;
            return null;
        });
        droppingClient.CaptureMessage("drop", DebugBundleLogLevel.Error);
        await droppingClient.FlushAsync();
        Assert.Equal(1, calls);
        Assert.Equal(0, dropTransport.Calls);

        var transport = new FakeTransport();
        var invalidClient = CreateClient(transport, envelope =>
        {
            envelope.EventId = "invalid";
            return envelope;
        });
        invalidClient.CaptureMessage("preserve invalid", DebugBundleLogLevel.Error);
        await invalidClient.FlushAsync();

        var failingClient = CreateClient(transport, envelope => throw new InvalidOperationException("hook failed"));
        failingClient.CaptureMessage("preserve failure", DebugBundleLogLevel.Error);
        await failingClient.FlushAsync();

        Assert.Equal(2, transport.Calls);
        Assert.Equal("preserve invalid", transport.Batches[0].Single().Payload["message"]);
        Assert.Equal("preserve failure", transport.Batches[1].Single().Payload["message"]);

        var sampledCalls = 0;
        var sampledClient = DebugBundleClient.Create(new DebugBundleOptions
        {
            ProjectToken = "dbundle_proj_test",
            Transport = transport,
            SampleRate = 0,
            RandomSource = () => 1,
            BeforeSend = envelope =>
            {
                sampledCalls++;
                return envelope;
            }
        });
        sampledClient.CaptureMessage("sampled out", DebugBundleLogLevel.Error);
        await sampledClient.FlushAsync();
        Assert.Equal(1, sampledCalls);
        Assert.Equal(2, transport.Calls);
    }

    private static DebugBundleClient CreateClient(
        FakeTransport transport,
        Func<DebugBundleEventEnvelope, DebugBundleEventEnvelope?> hook)
    {
        return DebugBundleClient.Create(new DebugBundleOptions
        {
            ProjectToken = "dbundle_proj_test",
            Transport = transport,
            RandomSource = () => 0,
            BeforeSend = hook
        });
    }
}
