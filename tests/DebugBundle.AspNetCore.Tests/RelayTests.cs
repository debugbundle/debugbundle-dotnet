using System.Text;
using System.Text.Json;
using DebugBundle;
using DebugBundle.AspNetCore;
using DebugBundle.AspNetCore.Relay;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace DebugBundle.AspNetCore.Tests;

public sealed class RelayTests
{
    [Fact]
    public async Task Relay_Writes_Local_File_And_Strips_Credentials()
    {
        var root = Path.Combine(Path.GetTempPath(), "debugbundle-dotnet-relay", Guid.NewGuid().ToString("N"));
        using var server = RelayServer(new DebugBundleOptions
        {
            ProjectToken = "dbundle_proj_server",
            ProjectMode = DebugBundleProjectMode.LocalOnly,
            LocalEventsDir = root,
            Service = "checkout-api",
            Environment = "test"
        });
        var client = server.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "http://localhost");

        var response = await client.PostAsync("/debugbundle/browser", new StringContent(BrowserBatch(), Encoding.UTF8, "application/json"));

        Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);
        var file = Assert.Single(Directory.EnumerateFiles(root, "*.events.json"));
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(file));
        var eventElement = document.RootElement[0];
        Assert.Equal("@debugbundle/sdk-browser", eventElement.GetProperty("sdk_name").GetString());
        Assert.False(eventElement.TryGetProperty("project_token", out _));
        Assert.False(eventElement.GetProperty("payload").TryGetProperty("authorization", out _));
        Assert.Equal("trace_browser", eventElement.GetProperty("correlation").GetProperty("trace_id").GetString());
    }

    [Fact]
    public async Task Relay_Rejects_Disallowed_Origins_Without_Cors_Headers()
    {
        using var server = RelayServer(new DebugBundleOptions
        {
            ProjectToken = "dbundle_proj_server",
            ProjectMode = DebugBundleProjectMode.LocalOnly,
            LocalEventsDir = Path.Combine(Path.GetTempPath(), "debugbundle-dotnet-relay", Guid.NewGuid().ToString("N"))
        });
        var request = new HttpRequestMessage(HttpMethod.Options, "/debugbundle/browser");
        request.Headers.TryAddWithoutValidation("Origin", "https://evil.example");
        request.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "POST");

        var response = await server.CreateClient().SendAsync(request);

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Relay_Answers_Allowed_Preflight()
    {
        using var server = RelayServer(new DebugBundleOptions
        {
            ProjectToken = "dbundle_proj_server",
            ProjectMode = DebugBundleProjectMode.LocalOnly,
            LocalEventsDir = Path.Combine(Path.GetTempPath(), "debugbundle-dotnet-relay", Guid.NewGuid().ToString("N"))
        });
        var request = new HttpRequestMessage(HttpMethod.Options, "/debugbundle/browser");
        request.Headers.TryAddWithoutValidation("Origin", "http://localhost");
        request.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "POST");

        var response = await server.CreateClient().SendAsync(request);

        Assert.Equal(System.Net.HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("http://localhost", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("POST, OPTIONS", response.Headers.GetValues("Access-Control-Allow-Methods").Single());
    }

    [Fact]
    public async Task Relay_Rejects_Unsupported_Content_Type_And_Oversized_Body()
    {
        using var server = RelayServer(new DebugBundleOptions
        {
            ProjectToken = "dbundle_proj_server",
            ProjectMode = DebugBundleProjectMode.LocalOnly,
            LocalEventsDir = Path.Combine(Path.GetTempPath(), "debugbundle-dotnet-relay", Guid.NewGuid().ToString("N"))
        });
        var client = server.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "http://localhost");

        var badContentType = await client.PostAsync("/debugbundle/browser", new StringContent("{}", Encoding.UTF8, "text/plain"));
        var oversized = await client.PostAsync("/debugbundle/browser", new StringContent(new string('x', 300 * 1024), Encoding.UTF8, "application/json"));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, badContentType.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
    }

    [Fact]
    public async Task Relay_Rate_Limits_By_Client_Ip()
    {
        using var server = RelayServer(new DebugBundleOptions
        {
            ProjectToken = "dbundle_proj_server",
            ProjectMode = DebugBundleProjectMode.LocalOnly,
            LocalEventsDir = Path.Combine(Path.GetTempPath(), "debugbundle-dotnet-relay", Guid.NewGuid().ToString("N"))
        }, relayOptions => relayOptions.RateLimitPerMinute = 1);
        var client = server.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "http://localhost");

        var first = await client.PostAsync("/debugbundle/browser", new StringContent(BrowserBatch(), Encoding.UTF8, "application/json"));
        var second = await client.PostAsync("/debugbundle/browser", new StringContent(BrowserBatch(), Encoding.UTF8, "application/json"));

        Assert.Equal(System.Net.HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal((System.Net.HttpStatusCode)429, second.StatusCode);
    }

    [Fact]
    public async Task Relay_Connected_Mode_Writes_Durable_Spool()
    {
        var root = Path.Combine(Path.GetTempPath(), "debugbundle-dotnet-relay-spool", Guid.NewGuid().ToString("N"));
        using var server = RelayServer(new DebugBundleOptions
        {
            ProjectToken = "dbundle_proj_server",
            ProjectMode = DebugBundleProjectMode.Connected,
            SpoolDir = root,
            Endpoint = new Uri("http://127.0.0.1:9/v1/events"),
            Service = "checkout-api",
            Environment = "test"
        });
        var client = server.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "http://localhost");

        var response = await client.PostAsync("/debugbundle/browser", new StringContent(BrowserBatch(), Encoding.UTF8, "application/json"));

        Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);
        Assert.Single(Directory.EnumerateFiles(root, "*.events.json"));
    }

    [Fact]
    public async Task Relay_Middleware_Surface_Handles_Post()
    {
        var root = Path.Combine(Path.GetTempPath(), "debugbundle-dotnet-relay-middleware", Guid.NewGuid().ToString("N"));
        using var server = new TestServer(new WebHostBuilder()
            .ConfigureServices(services =>
            {
                services.Configure<DebugBundleOptions>(options =>
                {
                    options.ProjectToken = "dbundle_proj_server";
                    options.ProjectMode = DebugBundleProjectMode.LocalOnly;
                    options.LocalEventsDir = root;
                    options.Service = "checkout-api";
                    options.Environment = "test";
                });
                services.Configure<DebugBundleRelayOptions>(_ => { });
                services.AddSingleton<IDebugBundleClient>(new FakeClient());
            })
            .Configure(app => app.UseDebugBundleBrowserRelay()));
        var client = server.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "http://localhost");

        var response = await client.PostAsync("/debugbundle/browser", new StringContent(BrowserBatch(), Encoding.UTF8, "application/json"));

        Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);
        Assert.Single(Directory.EnumerateFiles(root, "*.events.json"));
    }

    private static TestServer RelayServer(DebugBundleOptions sdkOptions, Action<DebugBundleRelayOptions>? configureRelay = null)
    {
        return new TestServer(new WebHostBuilder()
            .ConfigureServices(services =>
            {
                services.AddRouting();
                services.Configure<DebugBundleOptions>(options =>
                {
                    options.ProjectToken = sdkOptions.ProjectToken;
                    options.ProjectMode = sdkOptions.ProjectMode;
                    options.LocalEventsDir = sdkOptions.LocalEventsDir;
                    options.SpoolDir = sdkOptions.SpoolDir;
                    options.Service = sdkOptions.Service;
                    options.Environment = sdkOptions.Environment;
                });
                services.Configure<DebugBundleRelayOptions>(options => configureRelay?.Invoke(options));
                services.AddSingleton<IDebugBundleClient>(new FakeClient());
            })
            .Configure(app => app.UseRouting().UseEndpoints(endpoints => endpoints.MapDebugBundleBrowserRelay())));
    }

    private static string BrowserBatch()
    {
        return """
        {
          "batch": [
            {
              "schema_version": "1",
              "event_id": "evt_browser",
              "event_type": "frontend_exception",
              "sdk_name": "malicious",
              "sdk_version": "0.1.0",
              "project_token": "dbundle_proj_browser",
              "organization_id": "org_browser",
              "service": {
                "name": "browser",
                "runtime": "browser",
                "environment": "test"
              },
              "occurred_at": "2026-05-30T00:00:00.0000000Z",
              "correlation": {
                "trace_id": "trace_browser",
                "request_id": "req_browser"
              },
              "payload": {
                "message": "browser failed",
                "authorization": "Bearer browser-secret",
                "headers": {
                  "authorization": "Bearer browser-secret",
                  "content-type": "application/json"
                }
              }
            }
          ]
        }
        """;
    }
}
