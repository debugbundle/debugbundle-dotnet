using DebugBundle;

namespace DebugBundle.Sdk.Tests;

public sealed class BeforeSendProcessorValidationTests
{
    [Fact]
    public void Shared_Byte_Boundaries_Preserve_Owner_Policy_And_Invalid_Fallback()
    {
        using var corpus = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures/before-send-budget.json")));
        var original = System.Text.Json.JsonSerializer.Deserialize<DebugBundleEventEnvelope>(corpus.RootElement.GetProperty("original").GetRawText())!;
        foreach (var fixture in corpus.RootElement.GetProperty("cases").EnumerateArray())
        {
            var replacement = System.Text.Json.JsonSerializer.Deserialize<DebugBundleEventEnvelope>(fixture.GetProperty("event").GetRawText())!;
            var outcome = fixture.GetProperty("outcome").GetString();
            var result = BeforeSendProcessor.Apply(original, _ => replacement);
            Assert.NotNull(result);
            Assert.Equal(outcome == "fallback" ? "owner removed" : "owner replacement", result.Payload["message"]?.ToString());
            Assert.True(BeforeSendProcessor.IsValid(result));
            if (outcome == "fallback") continue;
            Assert.Equal(outcome == "keep", result.Payload.ContainsKey("exception_evidence"));
            Assert.Equal("preserved", result.Context!["owner_field"]?.ToString());
            var id = fixture.GetProperty("id").GetString()!;
            if (id.EndsWith("-legacy")) Assert.True(result.Context.ContainsKey("evidence_reference"));
            if (id.EndsWith("-typed") && outcome == "reduce") Assert.False(result.Context.ContainsKey("evidence_reference"));
        }
    }

    [Fact]
    public void Apply_Reduces_Combined_Evidence_Without_Restoring_Removed_Content()
    {
        using var corpus = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures/evidence-wire-cases.json")));
        var fixture = corpus.RootElement.GetProperty("events").EnumerateArray().Single(item => item.GetProperty("id").GetString() == "combined_optional_bytes");
        var candidate = System.Text.Json.JsonSerializer.Deserialize<DebugBundleEventEnvelope>(fixture.GetProperty("event").GetRawText())!;
        candidate.Payload["message"] = "owner replacement";
        var original = Envelope("log_event", new() { ["level"] = "error", ["message"] = "owner removed", ["attributes"] = new Dictionary<string, object?>() });
        var result = BeforeSendProcessor.Apply(original, _ => candidate);
        Assert.NotNull(result);
        Assert.Equal("owner replacement", result.Payload["message"]);
        Assert.True(BeforeSendProcessor.IsValid(result));
        Assert.False(result.Payload.ContainsKey("exception_evidence"));
        Assert.True(candidate.Payload.ContainsKey("exception_evidence"));
    }

    public static IEnumerable<object[]> ValidPayloads()
    {
        yield return Case("backend_exception", new()
        {
            ["name"] = "InvalidOperationException",
            ["message"] = "failed",
            ["stack"] = "stack",
            ["handled"] = true,
            ["request"] = new Dictionary<string, object?> { ["method"] = "GET", ["path"] = "/", ["query"] = new Dictionary<string, object?>(), ["headers"] = new Dictionary<string, object?>() },
            ["response"] = new Dictionary<string, object?> { ["status_code"] = 503 },
            ["runtime"] = new Dictionary<string, object?> { ["version"] = "fixture" },
            ["probe_data"] = new Dictionary<string, object?> { ["version"] = 1, ["items"] = new List<object?>() }
        });
        yield return Case("request_event", new()
        {
            ["method"] = "GET",
            ["path"] = "/failed",
            ["query"] = new Dictionary<string, object?>(),
            ["headers"] = new Dictionary<string, object?>(),
            ["response_headers"] = new Dictionary<string, object?>(),
            ["response_status"] = 503,
            ["duration_ms"] = 12.5
        });
        yield return Case("log_event", new()
        {
            ["level"] = "error",
            ["message"] = "failed",
            ["attributes"] = new Dictionary<string, object?>()
        });
        yield return Case("frontend_breadcrumb", new()
        {
            ["breadcrumb_type"] = "route_change",
            ["data"] = new Dictionary<string, object?>()
        });
        yield return Case("frontend_exception", new()
        {
            ["name"] = "Error",
            ["message"] = "failed",
            ["stack"] = "stack",
            ["browser"] = new Dictionary<string, object?> { ["name"] = "fixture", ["version"] = "1" },
            ["breadcrumbs"] = new List<object?>(),
            ["probe_data"] = new Dictionary<string, object?> { ["version"] = 1, ["items"] = new List<object?>() }
        });
        yield return Case("deploy_metadata", new()
        {
            ["commit_sha"] = "abc123",
            ["version"] = "1.2.3",
            ["branch"] = "main",
            ["environment"] = "production",
            ["deployed_at"] = DateTimeOffset.UtcNow.UtcDateTime.ToString("O")
        });
        yield return Case("error_suppressed", new()
        {
            ["fingerprint"] = "fingerprint",
            ["suppressed_count"] = 2,
            ["window_seconds"] = 60,
            ["first_seen"] = DateTimeOffset.UtcNow.AddMinutes(-1).UtcDateTime.ToString("O"),
            ["last_seen"] = DateTimeOffset.UtcNow.UtcDateTime.ToString("O")
        });
        yield return Case("probe_event", new()
        {
            ["label"] = "checkout.cart",
            ["data"] = new Dictionary<string, object?>(),
            ["activation_id"] = Guid.NewGuid().ToString(),
            ["probe_label_pattern"] = "checkout.*"
        });
    }

    [Theory]
    [MemberData(nameof(ValidPayloads))]
    public void Apply_Accepts_Every_Canonical_Event_Shape(
        string eventType,
        Dictionary<string, object?> payload)
    {
        var envelope = Envelope(eventType, payload);

        var result = BeforeSendProcessor.Apply(envelope, candidate =>
        {
            candidate.Context!["reviewed"] = true;
            return candidate;
        });

        Assert.NotSame(envelope, result);
        Assert.True((bool)result!.Context!["reviewed"]!);
    }

    [Fact]
    public void Apply_Clones_Nested_ReadOnly_Dictionaries_And_Lists()
    {
        IReadOnlyDictionary<string, object?> nested =
            new Dictionary<string, object?> { ["items"] = new[] { "one", "two" } };
        var envelope = Envelope("log_event", new()
        {
            ["level"] = "error",
            ["message"] = "failed",
            ["attributes"] = nested
        });

        var result = BeforeSendProcessor.Apply(envelope, candidate => candidate);

        Assert.NotSame(nested, result!.Payload["attributes"]);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("")]
    public void Apply_Preserves_The_Original_For_Unsupported_Event_Types(string eventType)
    {
        var envelope = Envelope(eventType, new());

        Assert.Same(envelope, BeforeSendProcessor.Apply(envelope, candidate => candidate));
    }

    private static object[] Case(string eventType, Dictionary<string, object?> payload) =>
        new object[] { eventType, payload };

    private static DebugBundleEventEnvelope Envelope(
        string eventType,
        Dictionary<string, object?> payload) =>
        new()
        {
            SchemaVersion = "2026-03-01",
            EventId = Guid.NewGuid().ToString(),
            EventType = eventType,
            SdkName = "@debugbundle/sdk-dotnet",
            SdkVersion = "1.3.0",
            Service = new DebugBundleServiceDescriptor
            {
                Name = "coverage-test",
                Runtime = ".NET",
                Environment = "test"
            },
            OccurredAt = DateTimeOffset.UtcNow.UtcDateTime.ToString("O"),
            Correlation = new Dictionary<string, object?> { ["trace_id"] = "trace" },
            Context = new Dictionary<string, object?> { ["items"] = new[] { 1, 2 } },
            Payload = payload
        };
}
