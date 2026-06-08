using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DebugBundle;
using DebugBundle.Transport;

namespace DebugBundle.Sdk.Tests;

public sealed class RemoteConfigAndProbeTests
{
    [Fact]
    public async Task Remote_Config_Enforces_Log_And_Request_Capture_Policy()
    {
        var transport = new FakeTransport();
        var fetcher = new FakeRemoteConfigFetcher();
        fetcher.Responses.Enqueue(_ => new RemoteConfigFetchResult
        {
            ETag = "\"policy\"",
            Config = new SdkRemoteConfig
            {
                CapturePolicy = new CapturePolicy
                {
                    Preset = "minimal",
                    CaptureLogs = "error",
                    CaptureRequestEvents = "off",
                    CaptureProbeEvents = "buffer_only"
                }
            }
        });
        var client = DebugBundleClient.Create(new DebugBundleOptions
        {
            ProjectToken = "dbundle_proj_test",
            Service = "checkout-api",
            Environment = "production",
            Transport = transport,
            RemoteConfigFetcher = fetcher,
            RandomSource = () => 0
        });

        client.CaptureLog("warning should drop", DebugBundleLogLevel.Warning);
        client.CaptureLog("error should ship", DebugBundleLogLevel.Error);
        client.CaptureRequest(new DebugBundleRequestInfo { Method = "GET", Path = "/ok" }, new DebugBundleResponseInfo { StatusCode = 200 });
        client.CaptureRequest(new DebugBundleRequestInfo { Method = "GET", Path = "/boom" }, new DebugBundleResponseInfo { StatusCode = 503 });
        await client.FlushAsync();

        var events = transport.Batches.Single();
        Assert.DoesNotContain(events, item => item.Payload.TryGetValue("message", out var value) && Equals(value, "warning should drop"));
        Assert.Contains(events, item => item.EventType == "log_event");
        Assert.Contains(events, item => item.EventType == "request_event" && Equals(item.Payload["path"], "/boom"));
        Assert.DoesNotContain(events, item => item.EventType == "request_event" && Equals(item.Payload["path"], "/ok"));
        Assert.Equal("checkout-api", fetcher.Requests.Single().Service);
        Assert.Equal("production", fetcher.Requests.Single().Environment);
    }

    [Fact]
    public async Task Remote_Config_Promotes_Path_Configured_Client_Error_When_Request_Capture_Is_Off()
    {
        var transport = new FakeTransport();
        var fetcher = new FakeRemoteConfigFetcher();
        fetcher.Responses.Enqueue(_ => new RemoteConfigFetchResult
        {
            Config = new SdkRemoteConfig
            {
                CapturePolicy = new CapturePolicy
                {
                    Preset = "minimal",
                    CaptureLogs = "error",
                    CaptureRequestEvents = "off",
                    CaptureProbeEvents = "buffer_only",
                    ImmediateClientErrorPathRules = new List<ImmediateClientErrorPathRule>
                    {
                        new()
                        {
                            StatusCode = 404,
                            PathPattern = "/checkout/*",
                            Methods = new List<string> { "POST" }
                        }
                    }
                }
            }
        });
        var client = DebugBundleClient.Create(new DebugBundleOptions
        {
            ProjectToken = "dbundle_proj_test",
            Service = "checkout-api",
            Environment = "production",
            Transport = transport,
            RemoteConfigFetcher = fetcher,
            RandomSource = () => 0
        });

        client.CaptureRequest(new DebugBundleRequestInfo { Method = "POST", Path = "/checkout/cart" }, new DebugBundleResponseInfo { StatusCode = 404 });
        client.CaptureRequest(new DebugBundleRequestInfo { Method = "GET", Path = "/checkout/cart" }, new DebugBundleResponseInfo { StatusCode = 404 });
        await client.FlushAsync();

        var requestEvent = transport.Batches.Single().Single(item => item.EventType == "request_event");
        Assert.Equal("/checkout/cart", requestEvent.Payload["path"]);
        Assert.Equal(404, requestEvent.Payload["response_status"]);
    }

    [Fact]
    public async Task Remote_Config_Ignores_Invalid_Path_Rule_Methods()
    {
        var transport = new FakeTransport();
        var fetcher = new FakeRemoteConfigFetcher();
        fetcher.Responses.Enqueue(_ => new RemoteConfigFetchResult
        {
            Config = new SdkRemoteConfig
            {
                CapturePolicy = new CapturePolicy
                {
                    Preset = "minimal",
                    CaptureLogs = "error",
                    CaptureRequestEvents = "off",
                    CaptureProbeEvents = "buffer_only",
                    ImmediateClientErrorPathRules = new List<ImmediateClientErrorPathRule>
                    {
                        new()
                        {
                            StatusCode = 404,
                            PathPattern = "/checkout/*",
                            Methods = new List<string> { "TRACE" }
                        }
                    }
                }
            }
        });
        var client = DebugBundleClient.Create(new DebugBundleOptions
        {
            ProjectToken = "dbundle_proj_test",
            Service = "checkout-api",
            Environment = "production",
            Transport = transport,
            RemoteConfigFetcher = fetcher,
            RandomSource = () => 0
        });

        client.CaptureRequest(new DebugBundleRequestInfo { Method = "TRACE", Path = "/checkout/cart" }, new DebugBundleResponseInfo { StatusCode = 404 });
        await client.FlushAsync();

        Assert.Empty(transport.Batches);
    }

    [Fact]
    public async Task Remote_Activated_Probe_Emits_Standalone_Event_And_Heavy_Probe()
    {
        var transport = new FakeTransport();
        var fetcher = new FakeRemoteConfigFetcher();
        fetcher.Responses.Enqueue(_ => new RemoteConfigFetchResult
        {
            Config = new SdkRemoteConfig
            {
                ProbesEnabled = true,
                RemoteProbesEnabled = true,
                ActiveProbes = new List<ProbeDirective>
                {
                    new()
                    {
                        ActivationId = "activation_123",
                        LabelPattern = "checkout.*",
                        Service = "checkout-api",
                        Environment = "production",
                        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
                    }
                },
                CapturePolicy = new CapturePolicy { CaptureProbeEvents = "standalone_when_activated" }
            }
        });
        var client = DebugBundleClient.Create(new DebugBundleOptions
        {
            ProjectToken = "dbundle_proj_test",
            Service = "checkout-api",
            Environment = "production",
            Transport = transport,
            RemoteConfigFetcher = fetcher,
            RandomSource = () => 0
        });

        var invoked = false;
        client.Probe("checkout.tax", () =>
        {
            invoked = true;
            return new { Amount = 42 };
        }, new ProbeOptions { Heavy = true });
        await client.FlushAsync();

        Assert.True(invoked);
        var probe = transport.Batches.Single().Single(item => item.EventType == "probe_event");
        Assert.Equal("activation_123", probe.Payload["activation_id"]);
        Assert.Equal("checkout.tax", probe.Payload["label"]);
    }

    [Fact]
    public async Task Trigger_Token_Activates_Single_Request_Probe()
    {
        var transport = new FakeTransport();
        var signingKey = "test-signing-key";
        var token = BuildTriggerToken(signingKey, new
        {
            activation_id = "trigger_activation",
            label_patterns = new[] { "checkout.trigger" },
            service = "checkout-api",
            environment = "production",
            trigger_expires_at = DateTimeOffset.UtcNow.AddMinutes(5)
        });
        var fetcher = new FakeRemoteConfigFetcher();
        fetcher.Responses.Enqueue(_ => new RemoteConfigFetchResult
        {
            Config = new SdkRemoteConfig
            {
                ProbesEnabled = true,
                RemoteProbesEnabled = true,
                TriggerTokenKey = signingKey,
                CapturePolicy = new CapturePolicy { CaptureProbeEvents = "standalone_when_activated" }
            }
        });
        var client = DebugBundleClient.Create(new DebugBundleOptions
        {
            ProjectToken = "dbundle_proj_test",
            Service = "checkout-api",
            Environment = "production",
            Transport = transport,
            RemoteConfigFetcher = fetcher,
            RandomSource = () => 0
        });

        using (client.BeginScope(new Dictionary<string, object?> { ["probe_trigger_token"] = token }))
        {
            client.Probe("checkout.trigger", () => new { Enabled = true }, new ProbeOptions { Heavy = true });
        }

        await client.FlushAsync();

        var probe = transport.Batches.Single().Single(item => item.EventType == "probe_event");
        Assert.Equal("trigger_activation", probe.Payload["activation_id"]);
    }

    [Fact]
    public async Task Probe_Flushes_With_Error_As_Versioned_Context()
    {
        var transport = new FakeTransport();
        var client = DebugBundleClient.Create(new DebugBundleOptions
        {
            ProjectToken = "dbundle_proj_test",
            Service = "checkout-api",
            Environment = "test",
            Transport = transport,
            RandomSource = () => 0
        });

        client.Probe("checkout.cart", new { Count = 2 });
        client.CaptureException(new InvalidOperationException("failed"));
        await client.FlushAsync();

        var exception = transport.Batches.Single().Single(item => item.EventType == "backend_exception");
        var probeData = Assert.IsType<Dictionary<string, object?>>(exception.Payload["probe_data"]);
        Assert.Equal(1, probeData["version"]);
        var items = Assert.IsAssignableFrom<IEnumerable<object?>>(probeData["items"]);
        var item = Assert.IsType<Dictionary<string, object?>>(items.Single());
        Assert.Equal("checkout.cart", item["label"]);
    }

    private static string BuildTriggerToken(string signingKey, object payload)
    {
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonSerialization.Options);
        var body = Base64Url(payloadBytes);
        byte[] signature;
        using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(signingKey)))
        {
            signature = hmac.ComputeHash(Encoding.ASCII.GetBytes(body));
        }

        return "dbundle_probe_" + body + "." + Base64Url(signature);
    }

    private static string Base64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
