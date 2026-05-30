using DebugBundle;
using DebugBundle.Transport;

namespace DebugBundle.Sdk.Tests;

public sealed class SuppressionAndBackoffTests
{
    [Fact]
    public async Task Duplicate_Suppression_Emits_Aggregate()
    {
        var transport = new FakeTransport();
        var client = DebugBundleClient.Create(new DebugBundleOptions
        {
            ProjectToken = "dbundle_proj_test",
            Service = "checkout-worker",
            Environment = "test",
            Transport = transport,
            BatchSize = 50,
            RandomSource = () => 0
        });

        for (var index = 0; index < 5; index++)
        {
            client.CaptureException(new InvalidOperationException("same failure"));
        }

        await client.FlushAsync();

        var events = transport.Batches.Single();
        Assert.Equal(3, events.Count(item => item.EventType == "backend_exception"));
        var aggregate = events.Single(item => item.EventType == "error_suppressed");
        Assert.Equal(2, aggregate.Payload["suppressed_count"]);
        Assert.NotNull(aggregate.Payload["fingerprint"]);
    }

    [Fact]
    public async Task Retryable_Response_Retains_Buffer_And_Backs_Off()
    {
        var transport = new FakeTransport();
        transport.EnqueueResponse(new EventTransportResult { StatusCode = 429, RetryAfter = TimeSpan.FromSeconds(30) });
        var client = DebugBundleClient.Create(new DebugBundleOptions
        {
            ProjectToken = "dbundle_proj_test",
            Service = "checkout-worker",
            Environment = "test",
            Transport = transport,
            RandomSource = () => 0
        });

        client.CaptureMessage("keep me", DebugBundleLogLevel.Warning);
        await client.FlushAsync();
        await client.FlushAsync();

        Assert.Equal(DebugBundleStatus.Degraded, client.Status);
        Assert.Equal(1, transport.Calls);
    }
}
