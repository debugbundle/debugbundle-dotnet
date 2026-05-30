using DebugBundle;

namespace DebugBundle.Sdk.Tests;

public sealed class UniversalInterfaceTests
{
    [Fact]
    public async Task Client_Exposes_Universal_Methods_And_Flushes_Events()
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

        using (client.BeginScope(new Dictionary<string, object?> { ["request_id"] = "req_123" }))
        {
            client.CaptureException(new InvalidOperationException("boom"));
            client.CaptureError(new ArgumentException("bad"));
            client.CaptureLog("retrying charge", DebugBundleLogLevel.Warning, new Dictionary<string, object?> { ["attempt"] = 2 });
            client.CaptureRequest(new DebugBundleRequestInfo
            {
                Method = "GET",
                Path = "/orders/123",
                RouteTemplate = "/orders/{id}",
                Headers = new Dictionary<string, string> { ["X-DebugBundle-Trace-Id"] = "trace_123", ["Authorization"] = "Bearer secret" }
            }, new DebugBundleResponseInfo { StatusCode = 500, Duration = TimeSpan.FromMilliseconds(42) });
            client.CaptureMessage("worker started");
            client.SetContext("tenant_id", "tenant_123");
            client.Probe("checkout.cart", new { ItemCount = 3 });
        }

        await client.FlushAsync();

        Assert.Equal(DebugBundleStatus.Healthy, client.Status);
        Assert.NotNull(client.LastEventAt);
        var events = transport.Batches.Single();
        Assert.Contains(events, item => item.EventType == "backend_exception");
        Assert.Contains(events, item => item.EventType == "request_event");
        Assert.Contains(events, item => item.EventType == "log_event");
        Assert.All(events, item => Assert.Equal("@debugbundle/sdk-dotnet", item.SdkName));
        var request = events.Single(item => item.EventType == "request_event");
        Assert.Equal("trace_123", request.Correlation!["trace_id"]);
        Assert.DoesNotContain("authorization", Assert.IsType<Dictionary<string, object?>>(request.Payload["headers"]!).Keys);
    }

    [Fact]
    public async Task Static_Facade_Is_Callable()
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

        global::DebugBundle.DebugBundle.CaptureMessage("facade message", DebugBundleLogLevel.Warning);
        global::DebugBundle.DebugBundle.SetContext("account_id", "acct_123");
        global::DebugBundle.DebugBundle.Probe("facade.probe", new { Ok = true });
        await global::DebugBundle.DebugBundle.FlushAsync();

        Assert.Equal(DebugBundleStatus.Healthy, global::DebugBundle.DebugBundle.Status);
        Assert.Contains(transport.Batches.Single(), item => item.EventType == "log_event");
    }
}
