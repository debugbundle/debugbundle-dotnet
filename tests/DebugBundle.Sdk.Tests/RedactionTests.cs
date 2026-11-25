using DebugBundle;
using DebugBundle.Redaction;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DebugBundle.Sdk.Tests;

public sealed class RedactionTests
{
    [Fact]
    public void Repeated_Privacy_Protection_Does_Not_Rebuild_Mandatory_Patterns()
    {
        const string input = "ordinary application failure";
        for (var index = 0; index < 3; index++) TelemetryPrivacy.Protect(input);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 50; index++) TelemetryPrivacy.Protect(input);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 50 * 100 * 1024, $"Repeated scalar protection allocated {allocated} bytes");
    }

    [Fact]
    public async Task Concurrent_Privacy_Policies_Keep_Custom_Fields_Literal_And_Isolated()
    {
        await Task.WhenAll(Enumerable.Range(0, 24).Select(index => Task.Run(() =>
        {
            var field = $"custom.{index}";
            var input = $"{field}=sensitive customX{index}=visible password=hidden";
            Assert.Equal($"{field}=[REDACTED] customX{index}=visible password=[REDACTED]",
                TelemetryPrivacy.Protect(input, new[] { field }));
            Assert.Equal($"{field}=sensitive customX{index}=visible password=[REDACTED]",
                TelemetryPrivacy.Protect(input));
        })));
    }

    [Fact]
    public void Protocol_Identity_Values_Must_Pass_Privacy_Checks()
    {
        var envelope = new DebugBundleEventEnvelope { Payload = new(), Correlation = new() { ["session_id"] = "session-123" } };
        Assert.NotNull(TelemetryPrivacy.ProtectEvent(envelope));
        envelope.Correlation["trace_id"] = "dbundle_proj_SYNTHETIC_SECRET";
        Assert.Null(TelemetryPrivacy.ProtectEvent(envelope));
        envelope.Correlation.Clear();
        envelope.SdkVersion = "password=SYNTHETIC_SECRET";
        Assert.Null(TelemetryPrivacy.ProtectEvent(envelope));
    }

    [Fact]
    public void Telemetry_Privacy_Matches_Shared_Conformance_Cases()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../fixtures/privacy-conformance.json"));
        using var fixture = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("telemetry-privacy-v1", fixture.RootElement.GetProperty("policy").GetString());
        foreach (var testCase in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            var safe = TelemetryPrivacy.Protect(testCase.GetProperty("input"));
            Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(safe),
                JsonNode.Parse(testCase.GetProperty("expected").GetRawText())), testCase.GetProperty("id").GetString());
        }
    }

    [Fact]
    public void Redactor_Scrubs_Default_And_Segment_Aware_Fields()
    {
        var redactor = new DebugBundleRedactor();
        var result = Assert.IsType<Dictionary<string, object?>>(redactor.Redact(new Dictionary<string, object?>
        {
            ["authorization"] = "Bearer secret",
            ["apiKey"] = "key_secret",
            ["safe"] = "visible",
            ["nested"] = new Dictionary<string, object?> { ["user_password"] = "p@ssw0rd" }
        }));

        Assert.Equal(DebugBundleRedactor.RedactedMarker, result["authorization"]);
        Assert.Equal(DebugBundleRedactor.RedactedMarker, result["apiKey"]);
        Assert.Equal("visible", result["safe"]);
        var nested = Assert.IsType<Dictionary<string, object?>>(result["nested"]);
        Assert.Equal(DebugBundleRedactor.RedactedMarker, nested["user_password"]);
    }

    [Fact]
    public void Redactor_Bounds_Circular_Objects()
    {
        var value = new Dictionary<string, object?>();
        value["self"] = value;

        var result = Assert.IsType<Dictionary<string, object?>>(new DebugBundleRedactor().Redact(value));

        Assert.Equal("[Circular]", result["self"]);
    }

    [Fact]
    public void Exact_Key_Redaction_Matches_Shared_Corpus_And_Enforces_Combined_Bounds()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../fixtures/privacy-conformance.json"));
        using var fixture = JsonDocument.Parse(File.ReadAllText(path));
        var exactCases = fixture.RootElement.GetProperty("exact_key_cases").EnumerateArray().ToList();
        Assert.NotEmpty(exactCases);
        foreach (var testCase in exactCases)
        {
            var additional = testCase.TryGetProperty("additional_keys", out var addProp) && addProp.ValueKind == JsonValueKind.Array
                ? addProp.EnumerateArray().Select(e => e.GetString()!).ToArray()
                : Array.Empty<string>();
            var exact = testCase.TryGetProperty("exact_keys", out var exactProp) && exactProp.ValueKind == JsonValueKind.Array
                ? exactProp.EnumerateArray().Select(e => e.GetString()!).ToArray()
                : Array.Empty<string>();
            var safe = TelemetryPrivacy.Protect(testCase.GetProperty("input"), additional, exact);
            Assert.True(
                JsonNode.DeepEquals(
                    JsonSerializer.SerializeToNode(safe),
                    JsonNode.Parse(testCase.GetProperty("expected").GetRawText())),
                testCase.GetProperty("id").GetString());
        }

        var literal = Assert.IsType<Dictionary<string, object?>>(TelemetryPrivacy.Protect(
            new Dictionary<string, object?>
            {
                ["a+b"] = "secret",
                ["aaab"] = "keep",
                ["k"] = "secret",
                ["K"] = "secret",
                ["\u212A"] = "keep"
            },
            Array.Empty<string>(),
            new[] { "a+b", "k" }));
        Assert.Equal("[REDACTED]", literal["a+b"]);
        Assert.Equal("keep", literal["aaab"]);
        Assert.Equal("[REDACTED]", literal["k"]);
        Assert.Equal("[REDACTED]", literal["K"]);
        Assert.Equal("keep", literal["\u212A"]);

        var broad65 = Enumerable.Range(0, 65).Select(i => $"broad_{i}").ToArray();
        var exact64 = Enumerable.Range(0, 64).Select(i => $"exact_{i}").ToArray();
        Assert.Throws<ArgumentException>(() => TelemetryPrivacy.Protect("ok", broad65, exact64));
        Assert.Throws<ArgumentException>(() => TelemetryPrivacy.Protect("ok", Array.Empty<string>(), new[] { "   " }));
        Assert.Throws<ArgumentException>(() => TelemetryPrivacy.Protect("ok", Array.Empty<string>(), new[] { new string('x', 65) }));
    }

    [Fact]
    public void ProtectEvent_Preserves_Validated_Protocol_Fields_Under_Broad_RedactFields()
    {
        var failureRef = Guid.NewGuid().ToString();
        var pageId = Guid.NewGuid().ToString();
        var envelope = new DebugBundleEventEnvelope
        {
            SchemaVersion = "2026-03-01",
            EventId = Guid.NewGuid().ToString(),
            EventType = "frontend_exception",
            SdkName = "@debugbundle/sdk-browser",
            SdkVersion = "3.0.0",
            OccurredAt = "2026-10-26T10:00:00Z",
            Service = new DebugBundleServiceDescriptor { Name = "web", Runtime = "browser", Environment = "production" },
            Context = new Dictionary<string, object?>
            {
                ["evidence_reference"] = new Dictionary<string, object?>
                {
                    ["version"] = 1,
                    ["failure_ref"] = failureRef,
                    ["scope"] = new Dictionary<string, object?> { ["kind"] = "page", ["page_id"] = pageId }
                },
                ["evidence_limits"] = new Dictionary<string, object?> { ["version"] = 1, ["execution"] = "unsupported" },
                ["payout"] = new Dictionary<string, object?>
                {
                    ["tag"] = "SYNTHETIC_TAG",
                    ["reference"] = "SYNTHETIC_REF",
                    ["reason"] = "SYNTHETIC_REASON",
                    ["file"] = "SYNTHETIC_FILE"
                }
            },
            Payload = new Dictionary<string, object?>
            {
                ["name"] = "Error",
                ["message"] = "boom reason=SYNTHETIC_REASON",
                ["browser"] = new Dictionary<string, object?> { ["name"] = "Chrome", ["version"] = "120" },
                ["browser_event"] = new Dictionary<string, object?>
                {
                    ["kind"] = "resource_error",
                    ["message"] = "failed reason=SYNTHETIC_REASON",
                    ["file_name"] = "https://cdn.example.test/app.js?file=SYNTHETIC_FILE",
                    ["line_number"] = 1,
                    ["column_number"] = 2,
                    ["target"] = new Dictionary<string, object?>
                    {
                        ["tag_name"] = "script",
                        ["source_url"] = "https://cdn.example.test/app.js?tag=SYNTHETIC_TAG",
                        ["attributes"] = new Dictionary<string, object?> { ["rel"] = "preload reason=SYNTHETIC_REASON", ["async"] = true }
                    },
                    ["opaque"] = true
                },
                ["rejection_reason"] = new Dictionary<string, object?> { ["kind"] = "error", ["message"] = "rejected reference=SYNTHETIC_REF" }
            }
        };

        var safe = TelemetryPrivacy.ProtectEvent(envelope, new[] { "tag", "reference", "reason", "file" }, new[] { "exact_secret" });
        Assert.NotNull(safe);
        var browserEvent = Assert.IsType<Dictionary<string, object?>>(safe!.Payload["browser_event"]);
        var target = Assert.IsType<Dictionary<string, object?>>(browserEvent["target"]);
        Assert.Equal("script", target["tag_name"]);
        var evidenceRef = Assert.IsType<Dictionary<string, object?>>(safe.Context!["evidence_reference"]);
        Assert.Equal(failureRef, evidenceRef["failure_ref"]);
        var payout = Assert.IsType<Dictionary<string, object?>>(safe.Context["payout"]);
        Assert.Equal("[REDACTED]", payout["tag"]);
        Assert.Equal("[REDACTED]", payout["reference"]);
        Assert.Equal("[REDACTED]", payout["reason"]);
        Assert.Equal("[REDACTED]", payout["file"]);

        // Non-allowlisted tag_name must not bypass broad RedactFields=["tag"].
        ((Dictionary<string, object?>)((Dictionary<string, object?>)envelope.Payload["browser_event"]!)["target"]!)["tag_name"] = "custom-widget";
        var safeCustom = TelemetryPrivacy.ProtectEvent(envelope, new[] { "tag" }, Array.Empty<string>());
        var customTarget = Assert.IsType<Dictionary<string, object?>>(Assert.IsType<Dictionary<string, object?>>(safeCustom!.Payload["browser_event"])["target"]);
        Assert.Equal("[REDACTED]", customTarget["tag_name"]);
    }

    [Fact]
    public void ProtectEvent_Preserves_Validated_Timing_And_Csp_Observations()
    {
        var envelope = ResourceObservationEvent("https://cdn.example.test/widget.js", "timing");
        var safe = TelemetryPrivacy.ProtectEvent(
            envelope,
            additionalFields: null,
            exactFields: new[] { "kind", "source_url", "response_status", "disposition" });

        Assert.NotNull(safe);
        Assert.True(JsonNode.DeepEquals(
            JsonSerializer.SerializeToNode(envelope.Payload),
            JsonSerializer.SerializeToNode(safe!.Payload)));
    }

    [Theory]
    [InlineData("https://cdn.example.test/widget.js?token=fixture", "timing")]
    [InlineData("https://cdn.example.test/widget.js", "unknown")]
    public void ProtectEvent_Rejects_Unvalidated_Resource_Observations(string sourceUrl, string firstKind)
    {
        Assert.Null(TelemetryPrivacy.ProtectEvent(ResourceObservationEvent(sourceUrl, firstKind)));
    }

    private static DebugBundleEventEnvelope ResourceObservationEvent(string sourceUrl, string firstKind)
    {
        const string observedAt = "2026-11-25T10:00:00Z";
        return new DebugBundleEventEnvelope
        {
            SchemaVersion = "2026-03-01",
            EventId = Guid.NewGuid().ToString(),
            EventType = "diagnostic_context",
            SdkName = "@debugbundle/sdk-browser",
            SdkVersion = "3.3.0",
            OccurredAt = observedAt,
            Service = new DebugBundleServiceDescriptor { Name = "web", Runtime = "browser", Environment = "test" },
            Correlation = new() { ["session_id"] = "browser-session" },
            Payload = new()
            {
                ["version"] = 1,
                ["kind"] = "resource_observation",
                ["failure_ref"] = Guid.NewGuid().ToString(),
                ["scope"] = new Dictionary<string, object?>
                {
                    ["kind"] = "page",
                    ["page_id"] = Guid.NewGuid().ToString()
                },
                ["source_event_id"] = Guid.NewGuid().ToString(),
                ["observations"] = new Dictionary<string, object?>
                {
                    ["version"] = 1,
                    ["items"] = new List<object?>
                    {
                        new Dictionary<string, object?>
                        {
                            ["kind"] = firstKind,
                            ["observed_at"] = observedAt,
                            ["source_url"] = sourceUrl,
                            ["availability"] = "observed",
                            ["response_status"] = 404,
                            ["duration_ms"] = 20,
                            ["transfer_size_bytes"] = 1024
                        },
                        new Dictionary<string, object?>
                        {
                            ["kind"] = "csp",
                            ["observed_at"] = observedAt,
                            ["source_url"] = null,
                            ["effective_directive"] = "script-src",
                            ["disposition"] = "report",
                            ["document_status_code"] = 200
                        }
                    }
                }
            }
        };
    }
}
