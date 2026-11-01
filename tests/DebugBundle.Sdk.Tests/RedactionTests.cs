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
}
