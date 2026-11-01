using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DebugBundle.AspNetCore.Relay;
using DebugBundle.Transport;
using Microsoft.AspNetCore.Http;

namespace DebugBundle.AspNetCore.Tests;

public sealed partial class RelayEvidenceTests
{
    private static JsonObject Fixture(string id = "native_graph")
    {
        var corpus = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures/evidence-wire-cases.json")))!;
        var item = (JsonObject)corpus["events"]!.AsArray().Single(value => value!["id"]!.GetValue<string>() == id)!["event"]!.DeepClone();
        item["sdk_name"] = "spoofed"; return item;
    }
    private static DebugBundleOptions Options(string root) => new() { ProjectMode = DebugBundleProjectMode.LocalOnly, LocalEventsDir = root, SpoolDir = root, ProjectToken = "dbundle_proj_server", Service = "server-owned", Environment = "test", EvidenceCapabilities = new EvidenceCapabilities { Features = new[] { "exception_evidence_v1", "recovery_v1", "resource_observations_v1", "evidence_provenance_v1" } } };
    private static string Root() => Path.Combine(Path.GetTempPath(), "debugbundle-relay-evidence", Guid.NewGuid().ToString("N"));
    private static async Task<DefaultHttpContext> Invoke(DebugBundleOptions options, DebugBundleRelayOptions relay, JsonArray batch, bool opted = true, bool query = false)
    {
        var context = new DefaultHttpContext(); context.Request.Scheme = "http"; context.Request.Host = new HostString("localhost"); context.Request.Method = "POST"; context.Request.Headers.Origin = "http://localhost"; context.Request.ContentType = "application/json";
        if (opted) { if (query) context.Request.QueryString = new QueryString("?debugbundle_evidence=1"); else context.Request.Headers["X-DebugBundle-Evidence"] = "1"; }
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(new JsonObject { ["batch"] = batch }.ToJsonString())); context.Response.Body = new MemoryStream();
        await DebugBundleRelayHandler.HandleAsync(context, options, relay); context.Response.Body.Position = 0; return context;
    }
    private static async Task<JsonDocument> Reply(DefaultHttpContext context) => await JsonDocument.ParseAsync(context.Response.Body);
    [Fact]
    public async Task OptedPartialAcceptancePreservesClosedEvidenceAndProtectedContext()
    {
        var root = Root(); var good = Fixture(); good["context"] = new JsonObject { ["evidence_reference"] = new JsonObject { ["version"] = 1, ["failure_ref"] = Guid.NewGuid().ToString("D"), ["scope"] = new JsonObject { ["kind"] = "operation", ["operation_id"] = Guid.NewGuid().ToString("D") } }, ["detail"] = "password=fictional-private" };
        var invalid = Fixture(); invalid["payload"]!["exception_evidence"]!["extra"] = true;
        var context = await Invoke(Options(root), new DebugBundleRelayOptions(), new JsonArray(good, invalid)); using var reply = await Reply(context);
        Assert.Equal(202, context.Response.StatusCode); Assert.Equal(1, reply.RootElement.GetProperty("accepted").GetInt32()); Assert.Equal(1, reply.RootElement.GetProperty("errors")[0].GetProperty("index").GetInt32()); Assert.Equal("invalid_event", reply.RootElement.GetProperty("errors")[0].GetProperty("reason").GetString()); Assert.Equal(4, reply.RootElement.GetProperty("evidence_capabilities").GetProperty("features").GetArrayLength());
        var stored = await File.ReadAllTextAsync(Assert.Single(Directory.EnumerateFiles(root, "*.events.json"))); Assert.DoesNotContain("fictional-private", stored);
        using var document = JsonDocument.Parse(stored); var item = document.RootElement[0]; Assert.True(item.GetProperty("payload").TryGetProperty("exception_evidence", out _)); Assert.True(item.GetProperty("context").TryGetProperty("evidence_reference", out _)); Assert.Equal("server-owned", item.GetProperty("service").GetProperty("name").GetString()); Assert.Equal("@debugbundle/sdk-browser", item.GetProperty("sdk_name").GetString()); Assert.False(item.TryGetProperty("project_token", out _));
    }
    [Fact]
    public async Task QueryOptInAcceptsRecoveryAndLegacyResponsesRetainStringsAndStatus()
    {
        var root = Root(); var options = Options(root); var relay = new DebugBundleRelayOptions();
        var context = await Invoke(options, relay, new JsonArray(Fixture("operation_recovery")), query: true); using var reply = await Reply(context); Assert.Equal(202, context.Response.StatusCode); Assert.True(reply.RootElement.TryGetProperty("evidence_capabilities", out _));
        context = await Invoke(options, relay, new JsonArray(42), opted: false); using var legacy = await Reply(context); Assert.Equal(400, context.Response.StatusCode); Assert.Equal(JsonValueKind.String, legacy.RootElement.GetProperty("errors")[0].ValueKind); Assert.False(legacy.RootElement.TryGetProperty("evidence_capabilities", out _));
    }
    private sealed class RecordingHandler(Func<int, JsonElement[], object> reply) : HttpMessageHandler
    {
        internal readonly List<JsonElement[]> Batches = new(); internal int Largest;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken); Largest = Math.Max(Largest, Encoding.UTF8.GetByteCount(body)); using var document = JsonDocument.Parse(body);
            var events = document.RootElement.GetProperty("events").EnumerateArray().Select(item => item.Clone()).ToArray(); Batches.Add(events);
            return new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent(JsonSerializer.Serialize(reply(Batches.Count, events))) };
        }
    }
    [Fact]
    public async Task ForwardingCountsFinalCredentialsAndWritesOnlyCompletelyAcknowledgedMarkers()
    {
        var root = Root(); var options = Options(root); options.ProjectMode = DebugBundleProjectMode.Connected;
        using var handler = new RecordingHandler((_, events) => new { accepted = events.Length, rejected = 0, errors = Array.Empty<object>(), evidence_capabilities = options.EvidenceCapabilities }); using var http = new HttpClient(handler); using var transport = new HttpEventTransport(options.Endpoint, TimeSpan.FromSeconds(5), http);
        var relay = new DebugBundleRelayOptions { MaxBodyBytes = 1024 * 1024, ForwardTransport = transport }; var batch = new JsonArray();
        for (var index = 0; index < 15; index++) { var item = Fixture(); item["event_id"] = Guid.NewGuid().ToString("D"); item["payload"]!["message"] = new string('é', 1500); item["payload"]!["stack"] = new string('é', 1500); batch.Add(item); }
        var context = await Invoke(options, relay, batch); Assert.Equal(202, context.Response.StatusCode); Assert.True(handler.Largest <= 262144); Assert.True(handler.Batches.Count > 1); Assert.Equal(15, handler.Batches.Sum(items => items.Length)); Assert.All(handler.Batches.SelectMany(items => items), item => Assert.Equal(options.ProjectToken, item.GetProperty("project_token").GetString()));
        var marker = Assert.Single(Directory.EnumerateFiles(root, "*.delivered")); if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(marker));
    }
    [Fact]
    public async Task PartialDownstreamAcknowledgementKeepsDurableSpoolPending()
    {
        var root = Root(); var options = Options(root); options.ProjectMode = DebugBundleProjectMode.Connected;
        using var handler = new RecordingHandler((_, _) => new { accepted = 0, rejected = 1, errors = new[] { new { index = 0, reason = "rate_limited" } } }); using var http = new HttpClient(handler); using var transport = new HttpEventTransport(options.Endpoint, TimeSpan.FromSeconds(5), http);
        var context = await Invoke(options, new DebugBundleRelayOptions { ForwardTransport = transport }, new JsonArray(Fixture())); Assert.Equal(202, context.Response.StatusCode); Assert.Single(Directory.EnumerateFiles(root, "*.events.json")); Assert.Empty(Directory.EnumerateFiles(root, "*.delivered"));
    }
}
