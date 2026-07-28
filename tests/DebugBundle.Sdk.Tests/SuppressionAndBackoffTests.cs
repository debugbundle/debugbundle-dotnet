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

    [Fact]
    public async Task Acknowledgement_Retries_Only_The_Indexed_Retryable_Rejection()
    {
        var transport = new FakeTransport();
        transport.EnqueueResponse(new EventTransportResult
        {
            StatusCode = 202,
            RetryAfter = TimeSpan.FromMilliseconds(1),
            Body = """
                {"accepted":1,"rejected":1,"errors":[{"index":1,"reason":"rate_limited"}]}
                """
        });
        transport.EnqueueResponse(new EventTransportResult
        {
            StatusCode = 202,
            Body = """{"accepted":1,"rejected":0,"errors":[]}"""
        });
        var client = CreateClient(transport);
        client.CaptureMessage("accepted", DebugBundleLogLevel.Warning);
        client.CaptureMessage("retry", DebugBundleLogLevel.Warning);

        await client.FlushAsync();

        Assert.Equal(DebugBundleStatus.Degraded, client.Status);
        Assert.NotNull(client.LastEventAt);
        await Task.Delay(20);
        await client.FlushAsync();

        Assert.Equal(2, transport.Calls);
        var retried = Assert.Single(transport.Batches[1]);
        Assert.Equal("retry", retried.Payload["message"]);
    }

    [Fact]
    public async Task All_Terminal_Rejections_Are_Removed_Without_Reporting_Delivery()
    {
        var transport = new FakeTransport();
        transport.EnqueueResponse(new EventTransportResult
        {
            StatusCode = 202,
            Body = """
                {"accepted":0,"rejected":1,"errors":[{"index":0,"reason":"capture_policy_rejected"}]}
                """
        });
        var client = CreateClient(transport);
        client.CaptureMessage("terminal", DebugBundleLogLevel.Warning);

        await client.FlushAsync();
        await client.FlushAsync();

        Assert.Equal(DebugBundleStatus.Disconnected, client.Status);
        Assert.Null(client.LastEventAt);
        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public async Task Inconsistent_Acknowledgement_Retains_The_Full_Batch()
    {
        var transport = new FakeTransport();
        transport.EnqueueResponse(new EventTransportResult
        {
            StatusCode = 202,
            RetryAfter = TimeSpan.FromMilliseconds(1),
            Body = """{"accepted":1,"rejected":0,"errors":[]}"""
        });
        transport.EnqueueResponse(new EventTransportResult
        {
            StatusCode = 202,
            Body = """{"accepted":2,"rejected":0,"errors":[]}"""
        });
        var client = CreateClient(transport);
        client.CaptureMessage("first", DebugBundleLogLevel.Warning);
        client.CaptureMessage("second", DebugBundleLogLevel.Warning);

        await client.FlushAsync();

        Assert.Equal(DebugBundleStatus.Degraded, client.Status);
        Assert.Null(client.LastEventAt);
        await Task.Delay(20);
        await client.FlushAsync();

        Assert.Equal(2, transport.Calls);
        Assert.Equal(2, transport.Batches[1].Count);
    }

    private static DebugBundleClient CreateClient(FakeTransport transport)
    {
        return DebugBundleClient.Create(new DebugBundleOptions
        {
            ProjectToken = "dbundle_proj_test",
            Service = "checkout-worker",
            Environment = "test",
            Transport = transport,
            BatchSize = 50,
            RandomSource = () => 0
        });
    }
}
