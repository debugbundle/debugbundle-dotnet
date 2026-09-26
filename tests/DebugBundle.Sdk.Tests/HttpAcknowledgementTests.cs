using System.Net;
using System.Reflection;
using DebugBundle.Transport;

namespace DebugBundle.Sdk.Tests;

public sealed class HttpAcknowledgementTests
{
    [Theory]
    [InlineData("1e300", 300000)]
    [InlineData("NaN", null)]
    [InlineData("Infinity", null)]
    [InlineData("-1", null)]
    [InlineData("0.25", 250)]
    public async Task HttpRetryHintIsBoundedBeforeDurationConversion(string header, int? expectedMs)
    {
        using var handler = new PlannedHandler("{}", header);
        using var http = new HttpClient(handler);
        using var transport = new HttpEventTransport(new Uri("https://example.invalid/events"), TimeSpan.FromSeconds(5), http);
        var previousCulture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
            var response = await transport.SendAsync(new EventTransportRequest());
            Assert.Equal(expectedMs, response.RetryAfter?.TotalMilliseconds);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previousCulture; }
    }

    [Theory]
    [InlineData(429, null)]
    [InlineData(202, "{\"accepted\":2,\"rejected\":0,\"errors\":[]}")]
    [InlineData(202, "{\"accepted\":0,\"rejected\":1,\"errors\":[{\"index\":0,\"reason\":\"rate_limited\"}]}")]
    public async Task CustomRetryHintIsBounded(int status, string? body)
    {
        var transport = new FakeTransport();
        transport.EnqueueResponse(new EventTransportResult { StatusCode = status, Body = body, RetryAfter = TimeSpan.MaxValue });
        using var client = DebugBundleClient.Create(new DebugBundleOptions
        {
            ProjectToken = "dbundle_proj_test",
            Transport = transport,
            BatchSize = 100,
            FlushInterval = TimeSpan.FromHours(1)
        });
        client.CaptureMessage("retry", DebugBundleLogLevel.Error);
        await client.FlushAsync();
        var deadline = (DateTimeOffset?)typeof(DebugBundleClient).GetField("_retryUntil", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client);
        Assert.NotNull(deadline);
        var remaining = deadline.Value - DateTimeOffset.UtcNow;
        Assert.InRange(remaining, TimeSpan.FromSeconds(299), TimeSpan.FromMinutes(5));
    }

    [Theory]
    [InlineData("{\"accepted\":null,\"rejected\":2,\"errors\":[{\"index\":0,\"reason\":\"rate_limited\"},{\"index\":1,\"reason\":\"rate_limited\"}]}")]
    [InlineData("{\"accepted\":2,\"rejected\":0,\"errors\":null}")]
    [InlineData("{\"accepted\":1,\"rejected\":1,\"errors\":[{\"index\":4294967296,\"reason\":\"rate_limited\"}]}")]
    [InlineData("{\"accepted\":1,\"rejected\":1,\"errors\":{\"one\":{\"index\":1,\"reason\":\"rate_limited\"}}}")]
    [InlineData("")]
    [InlineData("<html>proxy</html>")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"accepted\":1,\"rejected\":0,\"errors\":[]}")]
    [InlineData("{\"accepted\":0,\"rejected\":2,\"errors\":[{\"index\":0,\"reason\":\"rate_limited\"},{\"index\":0,\"reason\":\"rate_limited\"}]}")]
    [InlineData("{\"accepted\":1,\"rejected\":1,\"errors\":[{\"index\":2,\"reason\":\"rate_limited\"}]}")]
    public async Task BuiltInHttpRetainsUnacknowledgedBatchAndRecovers(string body)
    {
        using var handler = new PlannedHandler(body);
        using var http = new HttpClient(handler);
        using var transport = new HttpEventTransport(new Uri("https://example.invalid/events"), TimeSpan.FromSeconds(5), http);
        using var client = DebugBundleClient.Create(new DebugBundleOptions
        {
            ProjectToken = "dbundle_proj_test",
            Transport = transport,
            BatchSize = 100,
            FlushInterval = TimeSpan.FromHours(1),
            RandomSource = () => 0
        });
        client.CaptureMessage("first", DebugBundleLogLevel.Error);
        client.CaptureMessage("second", DebugBundleLogLevel.Error);
        await client.FlushAsync();
        Assert.Null(client.LastEventAt);
        Assert.Equal(DebugBundleStatus.Degraded, client.Status);
        await client.FlushAsync();
        Assert.Single(handler.Batches);
        typeof(DebugBundleClient).GetField("_retryUntil", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(client, null);
        await client.FlushAsync();
        Assert.Equal(2, handler.Batches.Count);
        Assert.Equal(handler.Batches[0], handler.Batches[1]);
        Assert.NotNull(client.LastEventAt);
    }

    private sealed class PlannedHandler(string body, string retryAfter = "300") : HttpMessageHandler
    {
        public List<string> Batches { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Batches.Add(await request.Content!.ReadAsStringAsync());
            var response = new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent(Batches.Count == 1 ? body : "{\"accepted\":2,\"rejected\":0,\"errors\":[]}")
            };
            response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
            return response;
        }
    }
}
