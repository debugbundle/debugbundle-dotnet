using DebugBundle;

namespace DebugBundle.Sdk.Tests;

public sealed class BeforeSendProcessorValidationTests
{
    public static IEnumerable<object[]> ValidPayloads()
    {
        yield return Case("backend_exception", new()
        {
            ["name"] = "InvalidOperationException",
            ["message"] = "failed",
            ["stack"] = "stack",
            ["handled"] = true,
            ["request"] = new Dictionary<string, object?>(),
            ["response"] = new Dictionary<string, object?>(),
            ["runtime"] = new Dictionary<string, object?>(),
            ["probe_data"] = new Dictionary<string, object?>()
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
            ["breadcrumb_type"] = "navigation",
            ["data"] = new Dictionary<string, object?>()
        });
        yield return Case("frontend_exception", new()
        {
            ["name"] = "Error",
            ["message"] = "failed",
            ["stack"] = "stack",
            ["breadcrumbs"] = new List<object?>(),
            ["probe_data"] = new Dictionary<string, object?>()
        });
        yield return Case("deploy_metadata", new()
        {
            ["commit_sha"] = "abc123",
            ["version"] = "1.2.3",
            ["branch"] = "main",
            ["environment"] = "production",
            ["deployed_at"] = DateTimeOffset.UtcNow.ToString("O")
        });
        yield return Case("error_suppressed", new()
        {
            ["fingerprint"] = "fingerprint",
            ["suppressed_count"] = 2,
            ["window_seconds"] = 60,
            ["first_seen"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"),
            ["last_seen"] = DateTimeOffset.UtcNow.ToString("O")
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
            OccurredAt = DateTimeOffset.UtcNow.ToString("O"),
            Correlation = new Dictionary<string, object?> { ["trace_id"] = "trace" },
            Context = new Dictionary<string, object?> { ["items"] = new[] { 1, 2 } },
            Payload = payload
        };
}
