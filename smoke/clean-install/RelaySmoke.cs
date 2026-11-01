using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DebugBundle;
using DebugBundle.AspNetCore;
using DebugBundle.AspNetCore.Relay;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

internal static class RelaySmoke
{
    internal static async Task RunAsync()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        var endpoint = new Uri($"http://127.0.0.1:{port}/v1/events"); var receiving = ReceiveAsync(listener);
        var root = Path.Combine(Path.GetTempPath(), "debugbundle-installed-relay", Guid.NewGuid().ToString("N"));
        using var inactiveClient = DebugBundleClient.Create(new DebugBundleOptions { Enabled = false });
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IDebugBundleClient>(inactiveClient);
        builder.Services.Configure<DebugBundleOptions>(options =>
        {
            options.ProjectToken = "dbundle_proj_installed_relay_fixture"; options.Endpoint = endpoint; options.ProjectMode = DebugBundleProjectMode.Connected; options.SpoolDir = root; options.Service = "installed-relay"; options.Environment = "test";
            options.EvidenceCapabilities = new EvidenceCapabilities { Features = new[] { "exception_evidence_v1", "resource_observations_v1", "recovery_v1" } };
        });
        builder.Services.Configure<DebugBundleRelayOptions>(_ => { });
        await using var app = builder.Build(); app.MapDebugBundleBrowserRelay(); await app.StartAsync();
        var origin = app.Urls.Single(); using var browser = new HttpClient();
        var eventId = Guid.NewGuid().ToString("D"); var body = BrowserBody(eventId);
        using var request = new HttpRequestMessage(HttpMethod.Post, origin + "/debugbundle/browser?debugbundle_evidence=1") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("Origin", origin); using var response = await browser.SendAsync(request); using var acknowledgement = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (response.StatusCode != HttpStatusCode.Accepted || acknowledgement.RootElement.GetProperty("accepted").GetInt32() != 1 || acknowledgement.RootElement.GetProperty("rejected").GetInt32() != 0 || acknowledgement.RootElement.GetProperty("evidence_capabilities").GetProperty("features").GetArrayLength() != 3) throw new InvalidOperationException("Installed ASP.NET relay acknowledgement failed.");
        var forwarded = await receiving.WaitAsync(TimeSpan.FromSeconds(10));
        if (forwarded.Contains("RELAY_ARTIFACT_CANARY", StringComparison.Ordinal) || forwarded.Contains("dbundle_proj_browser_untrusted", StringComparison.Ordinal)) throw new InvalidOperationException("Installed ASP.NET relay leaked application data or browser credentials.");
        using var document = JsonDocument.Parse(forwarded); var item = document.RootElement.GetProperty("events")[0];
        if (item.GetProperty("event_id").GetString() != eventId || item.GetProperty("sdk_name").GetString() != "@debugbundle/sdk-browser" || item.GetProperty("service").GetProperty("name").GetString() != "installed-relay" || item.GetProperty("project_token").GetString() != "dbundle_proj_installed_relay_fixture" || !item.GetProperty("payload").TryGetProperty("exception_evidence", out _) || !item.GetProperty("payload").TryGetProperty("resource_observations", out _) || !item.GetProperty("context").TryGetProperty("evidence_reference", out _)) throw new InvalidOperationException("Installed ASP.NET relay lost evidence or server ownership.");
        var spool = Directory.EnumerateFiles(root, "*.events.json").Single(); using var retained = JsonDocument.Parse(await File.ReadAllTextAsync(spool));
        if (retained.RootElement[0].TryGetProperty("project_token", out _) || !File.Exists(spool + ".delivered")) throw new InvalidOperationException("Installed relay durable spool isolation or complete-ACK marker failed.");
        await app.StopAsync(); Console.WriteLine("Clean installed ASP.NET route preserved browser evidence, privacy, server identity, native forwarding and durable ACK marker.");
    }
    private static string BrowserBody(string eventId) => JsonSerializer.Serialize(new
    {
        batch = new[] { new {
            schema_version = "2026-03-01", event_id = eventId, event_type = "frontend_exception", sdk_name = "untrusted-browser-name", sdk_version = "3.2.0", project_token = "dbundle_proj_browser_untrusted", organization_id = "untrusted-organisation",
            occurred_at = DateTimeOffset.UtcNow.UtcDateTime.ToString("O"), service = new { name = "untrusted-service", environment = "untrusted", runtime = "browser" }, correlation = new { session_id = "0" },
            context = new { detail = "password=RELAY_ARTIFACT_CANARY", evidence_reference = new { version = 1, failure_ref = Guid.NewGuid().ToString("D"), scope = new { kind = "operation", operation_id = Guid.NewGuid().ToString("D") } } },
            payload = new { name = "Error", message = "fictional browser primary", stack = "Error: fictional browser primary", browser = new { name = "fixture", version = "1" },
                exception_evidence = new { version = 1, runtime = "browser", root_id = 0, nodes = new[] { new { id = 0, name = "Error", message = "fictional browser primary", stack = (string?)null, trace_origin = "runtime_reported", parsing_status = "parsed", frames = new[] { new { function = "run", file = "/app.js", line = 8, column = (int?)null, classification = "unknown" } }, relationships = Array.Empty<object>() } }, truncated = false, unavailable_reason = (string?)null },
                resource_observations = new { version = 1, items = new[] { new { kind = "timing", observed_at = DateTimeOffset.UtcNow.UtcDateTime.ToString("O"), source_url = "/image.png", availability = "unsupported", response_status = (int?)null, duration_ms = (double?)null, transfer_size_bytes = (int?)null } } }
            }
        } }
    });
    private static async Task<string> ReceiveAsync(HttpListener listener)
    {
        var context = await listener.GetContextAsync();
        if (context.Request.HttpMethod != "POST" || context.Request.Headers["Authorization"] != "Bearer dbundle_proj_installed_relay_fixture") throw new InvalidOperationException("Installed relay bearer scope failed.");
        using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding); var body = await reader.ReadToEndAsync(); using var document = JsonDocument.Parse(body);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { accepted = document.RootElement.GetProperty("events").GetArrayLength(), rejected = 0, errors = Array.Empty<object>(), evidence_capabilities = new { version = 1, features = new[] { "exception_evidence_v1", "resource_observations_v1", "recovery_v1" } } }));
        context.Response.StatusCode = 202; context.Response.ContentType = "application/json"; context.Response.ContentLength64 = bytes.Length; await context.Response.OutputStream.WriteAsync(bytes); context.Response.Close(); return body;
    }
}
