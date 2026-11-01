using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using DebugBundle;
using DebugBundle.Transport;

namespace DebugBundle.Sdk.Tests;

public sealed class FinalizedDeliveryTests
{
    private static void PermitRetry(DebugBundleClient client) => typeof(DebugBundleClient).GetField("_retryUntil", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(client, null);
    [Fact]
    public async Task RetryRetainsTheAcceptedHookIdentityAndDoesNotInvokeHookAgain()
    {
        var transport = new FakeTransport(); transport.EnqueueResponse(new EventTransportResult { StatusCode = 503, RetryAfter = TimeSpan.FromHours(1) });
        var hooks = 0;
        using var client = DebugBundleClient.Create(new DebugBundleOptions { ProjectMode = DebugBundleProjectMode.LocalOnly, Transport = transport, BeforeSend = candidate => { hooks++; candidate.EventId = Guid.NewGuid().ToString("D"); return candidate; } });
        client.CaptureMessage("fixture", DebugBundleLogLevel.Error); await client.FlushAsync(); PermitRetry(client); await client.FlushAsync();
        Assert.Equal(2, transport.Batches.Count); Assert.Equal(1, hooks);
        Assert.Equal(transport.Batches[0][0].EventId, transport.Batches[1][0].EventId);
    }
    private sealed class RecordingHandler(Func<int, JsonElement[], object> reply) : HttpMessageHandler
    {
        internal readonly List<JsonElement[]> Batches = new(); internal int Largest;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken); Largest = Math.Max(Largest, Encoding.UTF8.GetByteCount(body));
            using var document = JsonDocument.Parse(body); var events = document.RootElement.GetProperty("events").EnumerateArray().Select(item => item.Clone()).ToArray();
            Batches.Add(events);
            return new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent(JsonSerializer.Serialize(reply(Batches.Count, events))) };
        }
    }
    [Fact]
    public async Task NativeSenderSplitsFinalEscapedBytesAndKeepsAllFinalizedTailsCharged()
    {
        using var handler = new RecordingHandler((_, events) => new { accepted = events.Length, rejected = 0, errors = Array.Empty<object>() });
        using var http = new HttpClient(handler); using var transport = new HttpEventTransport(new Uri("https://example.invalid/events"), TimeSpan.FromSeconds(5), http); var hooks = 0;
        using var client = DebugBundleClient.Create(new DebugBundleOptions { ProjectMode = DebugBundleProjectMode.LocalOnly, ProjectToken = "synthetic", Transport = transport, BatchSize = 512, FlushInterval = TimeSpan.FromHours(1), BeforeSend = candidate => { hooks++; return candidate; } });
        for (var index = 0; index < 25; index++) client.CaptureLog($"fixture {index}", DebugBundleLogLevel.Error, Enumerable.Range(0, 8).ToDictionary(field => $"detail_{field}", _ => (object?)new string('é', 1500)));
        await client.FlushAsync();
        Assert.True(handler.Batches.Count > 1); Assert.True(handler.Largest <= 262144); Assert.Equal(25, handler.Batches.Sum(batch => batch.Length)); Assert.Equal(25, hooks);
    }
    [Fact]
    public async Task NativeACKDiscoversSupportWithoutExtraRequests()
    {
        using var handler = new RecordingHandler((_, events) => new { accepted = events.Length, rejected = 0, errors = Array.Empty<object>(), evidence_capabilities = new { version = 1, features = new[] { "exception_evidence_v1" } } });
        using var http = new HttpClient(handler); using var transport = new HttpEventTransport(new Uri("https://example.invalid/events"), TimeSpan.FromSeconds(5), http);
        using var client = DebugBundleClient.Create(new DebugBundleOptions { ProjectMode = DebugBundleProjectMode.LocalOnly, ProjectToken = "synthetic", Transport = transport });
        var first = new Exception("first"); client.CaptureException(first); await client.FlushAsync();
        var second = new Exception("second"); client.CaptureException(second); await client.FlushAsync(); GC.KeepAlive(first); GC.KeepAlive(second);
        Assert.Equal(2, handler.Batches.Count); Assert.False(handler.Batches[0][0].GetProperty("payload").TryGetProperty("exception_evidence", out _)); Assert.True(handler.Batches[1][0].GetProperty("payload").TryGetProperty("exception_evidence", out _));
    }
    [Fact]
    public async Task RejectedOriginalDowngradesOnceAfterProofLossWithoutRepeatingHook()
    {
        using var handler = new RecordingHandler((call, _) => call == 1 ? new { accepted = 0, rejected = 1, errors = new[] { new { index = 0, reason = "invalid_event" } } } : (object)new { accepted = 1, rejected = 0, errors = Array.Empty<object>() });
        using var http = new HttpClient(handler); using var transport = new HttpEventTransport(new Uri("https://example.invalid/events"), TimeSpan.FromSeconds(5), http); var hooks = 0;
        using var client = DebugBundleClient.Create(new DebugBundleOptions { ProjectMode = DebugBundleProjectMode.LocalOnly, ProjectToken = "synthetic", Transport = transport, EvidenceCapabilities = new EvidenceCapabilities { Features = new[] { "exception_evidence_v1" } }, BeforeSend = candidate => { hooks++; candidate.Payload["message"] = "latest protected primary"; return candidate; } });
        var failure = new Exception("private original"); client.CaptureException(failure); await client.FlushAsync(); Assert.Null(client.LastEventAt); PermitRetry(client); await client.FlushAsync(); GC.KeepAlive(failure);
        Assert.Equal(2, handler.Batches.Count); Assert.Equal(1, hooks); Assert.NotNull(client.LastEventAt);
        Assert.Equal(handler.Batches[0][0].GetProperty("event_id").GetString(), handler.Batches[1][0].GetProperty("event_id").GetString());
        Assert.Equal("latest protected primary", handler.Batches[1][0].GetProperty("payload").GetProperty("message").GetString()); Assert.False(handler.Batches[1][0].GetProperty("payload").TryGetProperty("exception_evidence", out _));
    }
}
