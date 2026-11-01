using System.Text.Json;
using System.Text.Json.Nodes;
using DebugBundle.AspNetCore.Relay;
using DebugBundle.Transport;

namespace DebugBundle.AspNetCore.Tests;

public sealed partial class RelayEvidenceTests
{
    [Fact]
    public async Task ExistingNativeAcknowledgementDiscoversThenRevokesReaderSupport()
    {
        var root = Root(); var options = Options(root); options.ProjectMode = DebugBundleProjectMode.Connected; var capabilities = options.EvidenceCapabilities; options.EvidenceCapabilities = null;
        using var handler = new RecordingHandler((call, events) => new { accepted = events.Length, rejected = 0, errors = Array.Empty<object>(), evidence_capabilities = call == 1 ? capabilities : null });
        using var http = new HttpClient(handler); using var transport = new HttpEventTransport(options.Endpoint, TimeSpan.FromSeconds(5), http); var relay = new DebugBundleRelayOptions { ForwardTransport = transport };
        var first = await Invoke(options, relay, new JsonArray(Fixture())); using var firstReply = await Reply(first);
        Assert.Equal(4, firstReply.RootElement.GetProperty("evidence_capabilities").GetProperty("features").GetArrayLength());
        var second = await Invoke(options, relay, new JsonArray(Fixture())); using var secondReply = await Reply(second); Assert.Equal(0, secondReply.RootElement.GetProperty("evidence_capabilities").GetProperty("features").GetArrayLength());
        Assert.False(handler.Batches[0][0].GetProperty("payload").TryGetProperty("exception_evidence", out _)); Assert.True(handler.Batches[1][0].GetProperty("payload").TryGetProperty("exception_evidence", out _));
        var third = await Invoke(options, relay, new JsonArray(Fixture())); Assert.Equal(202, third.Response.StatusCode); Assert.False(handler.Batches[2][0].GetProperty("payload").TryGetProperty("exception_evidence", out _));
    }
    [Fact]
    public async Task ProofLossRetriesOnlyExplicitlyRejectedOriginalWithSameIdentity()
    {
        var root = Root(); var options = Options(root); options.ProjectMode = DebugBundleProjectMode.Connected;
        using var handler = new RecordingHandler((call, events) => call == 1 ? new { accepted = 0, rejected = 1, errors = new[] { new { index = 0, reason = "invalid_event" } } } : (object)new { accepted = events.Length, rejected = 0, errors = Array.Empty<object>() });
        using var http = new HttpClient(handler); using var transport = new HttpEventTransport(options.Endpoint, TimeSpan.FromSeconds(5), http);
        var response = await Invoke(options, new DebugBundleRelayOptions { ForwardTransport = transport }, new JsonArray(Fixture())); Assert.Equal(202, response.Response.StatusCode); Assert.Equal(2, handler.Batches.Count);
        Assert.Equal(handler.Batches[0][0].GetProperty("event_id").GetString(), handler.Batches[1][0].GetProperty("event_id").GetString()); Assert.False(handler.Batches[1][0].GetProperty("payload").TryGetProperty("exception_evidence", out _)); Assert.Single(Directory.EnumerateFiles(root, "*.delivered"));
    }
    private sealed class CallbackTransport(Func<EventTransportRequest, Task<EventTransportResult>> callback) : IEventTransport
    {
        public Task<EventTransportResult> SendAsync(EventTransportRequest request, CancellationToken cancellationToken = default) => callback(request);
    }
    [Fact]
    public async Task LegacyCustomSuccessKeepsSpoolsPendingAndExclusiveMarkersDoNotFollowLinks()
    {
        var root = Root(); var options = Options(root); options.ProjectMode = DebugBundleProjectMode.Connected; var calls = 0;
        var response = await Invoke(options, new DebugBundleRelayOptions { ForwardTransport = new CallbackTransport(_ => { calls++; return Task.FromResult(new EventTransportResult { StatusCode = 202 }); }) }, new JsonArray(Fixture())); Assert.Equal(202, response.Response.StatusCode); Assert.Equal(1, calls); Assert.Empty(Directory.EnumerateFiles(root, "*.delivered"));
        if (OperatingSystem.IsWindows()) return;
        root = Root(); options.SpoolDir = root; var target = Path.Combine(Path.GetTempPath(), "debugbundle-marker-target-" + Guid.NewGuid().ToString("N")); await File.WriteAllTextAsync(target, "preserved");
        var transport = new CallbackTransport(request =>
        {
            calls++; File.CreateSymbolicLink(Assert.Single(Directory.EnumerateFiles(root, "*.events.json")) + ".delivered", target);
            return Task.FromResult(new EventTransportResult { StatusCode = 202, Body = "{\"accepted\":" + request.Events.Count + ",\"rejected\":0,\"errors\":[]}" });
        });
        response = await Invoke(options, new DebugBundleRelayOptions { ForwardTransport = transport }, new JsonArray(Fixture())); Assert.Equal(202, response.Response.StatusCode); Assert.Equal(2, calls); Assert.Equal("preserved", await File.ReadAllTextAsync(target));
    }
    [Fact]
    public async Task OneHeldCustomForwarderCannotSpawnReplacementWorkersPastSharedDeadline()
    {
        var root = Root(); var options = Options(root); options.ProjectMode = DebugBundleProjectMode.Connected; options.RequestTimeout = TimeSpan.FromMilliseconds(80);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource<EventTransportResult>(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        var relay = new DebugBundleRelayOptions { ForwardTransport = new CallbackTransport(_ => { Interlocked.Increment(ref calls); entered.TrySetResult(true); return release.Task; }) };
        try
        {
            var pending = Invoke(options, relay, new JsonArray(Fixture())); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var first = await pending.WaitAsync(TimeSpan.FromSeconds(3)); var second = await Invoke(options, relay, new JsonArray(Fixture())).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(202, first.Response.StatusCode); Assert.Equal(202, second.Response.StatusCode); Assert.Equal(1, calls); Assert.Empty(Directory.EnumerateFiles(root, "*.delivered"));
        }
        finally
        {
            release.TrySetResult(new EventTransportResult { StatusCode = 202 });
            var slot = (SemaphoreSlim)typeof(DebugBundleRelayHandler).GetField("CustomForwardSlot", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
            Assert.True(SpinWait.SpinUntil(() => slot.CurrentCount == 1, TimeSpan.FromSeconds(3)));
        }
    }
}
