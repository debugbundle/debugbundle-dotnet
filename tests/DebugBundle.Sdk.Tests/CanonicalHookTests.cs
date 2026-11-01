using System.Collections;
using DebugBundle;

namespace DebugBundle.Sdk.Tests;

public sealed class CanonicalHookTests
{
    private static DebugBundleEventEnvelope Primary() => new()
    {
        SchemaVersion = "2026-03-01",
        EventType = "backend_exception",
        OccurredAt = "2026-10-27T12:00:00Z",
        Payload = new()
        {
            ["name"] = "FixtureError",
            ["message"] = "protected primary",
            ["stack"] = "retained",
            ["handled"] = true,
            ["request"] = new Dictionary<string, object?> { ["method"] = "GET", ["path"] = "/", ["query"] = new Dictionary<string, object?>(), ["headers"] = new Dictionary<string, object?>() },
            ["response"] = new Dictionary<string, object?> { ["status_code"] = 503 },
            ["runtime"] = new Dictionary<string, object?> { ["version"] = "fixture" }
        }
    };

    [Fact]
    public void UnreadableInputSkipsHookAndInvalidOutputCannotReplaceProtectedPrimary()
    {
        var original = Primary(); var foreign = new ForeignEnumerable(); original.Context = new() { ["foreign"] = foreign }; var calls = 0;
        Assert.Same(original, BeforeSendProcessor.Apply(original, candidate => { calls++; return candidate; })); Assert.Equal(0, calls); Assert.Equal(0, foreign.Calls);
        original = Primary(); Assert.Same(original, BeforeSendProcessor.Apply(original, candidate => { candidate.Service = null!; return candidate; }));
        Assert.False(BeforeSendProcessor.IsValid(new DebugBundleEventEnvelope { Payload = null! }));
    }

    [Fact]
    public void InvalidNestedHookRecordRetainsProtectedOriginal()
    {
        var original = Primary();
        var result = BeforeSendProcessor.Apply(original, candidate =>
        {
            candidate.Payload["message"] = "invalid replacement";
            candidate.Payload["request"] = new Dictionary<string, object?>();
            return candidate;
        });
        Assert.Same(original, result);
        Assert.Equal("protected primary", original.Payload["message"]);
    }

    [Fact]
    public void ValidHookMayReplaceIdentityAndTypeButFinalResultIsOwned()
    {
        DebugBundleEventEnvelope? application = null;
        var id = Guid.NewGuid().ToString("D");
        var result = BeforeSendProcessor.Apply(Primary(), candidate =>
        {
            candidate.EventId = id;
            candidate.EventType = "log_event";
            candidate.Payload = new() { ["message"] = "replacement", ["level"] = "error", ["attributes"] = new Dictionary<string, object?>() };
            application = candidate;
            return candidate;
        });
        Assert.NotNull(result);
        Assert.NotSame(application, result);
        application!.Payload["message"] = "later mutation";
        Assert.Equal(id, result!.EventId);
        Assert.Equal("log_event", result.EventType);
        Assert.Equal("replacement", result.Payload["message"]);
    }

    private sealed class ForeignEnumerable : IEnumerable
    {
        internal int Calls;
        public IEnumerator GetEnumerator() { Calls++; return Array.Empty<object>().GetEnumerator(); }
    }

    [Fact]
    public void UnavailableOrExhaustedValidationWithholdsWithoutRestoringPrivateContent()
    {
        var foreign = new ForeignEnumerable();
        var result = BeforeSendProcessor.Apply(Primary(), candidate =>
        {
            candidate.Context = new() { ["foreign"] = foreign };
            return candidate;
        });
        Assert.Null(result);
        Assert.Equal(0, foreign.Calls);
        Assert.Null(BeforeSendProcessor.Apply(Primary(), candidate =>
        {
            candidate.Context = new() { ["large"] = new string('x', 8 * 1024 * 1024) };
            return candidate;
        }));
    }

    [Fact]
    public async Task NativeCaptureAndActivityMetadataFollowAcceptingTimestampAndCorrelationShape()
    {
        var transport = new FakeTransport();
        using var client = DebugBundleClient.Create(new DebugBundleOptions { ProjectToken = "synthetic", ProjectMode = DebugBundleProjectMode.LocalOnly, Transport = transport });
        using var activity = new System.Diagnostics.Activity("fixture").SetIdFormat(System.Diagnostics.ActivityIdFormat.W3C).Start();
        client.CaptureMessage("fixture", DebugBundleLogLevel.Error);
        await client.FlushAsync();
        var envelope = Assert.Single(transport.Batches.SelectMany(batch => batch));
        Assert.EndsWith("Z", envelope.OccurredAt);
        Assert.True(BeforeSendProcessor.IsValid(envelope));
        Assert.Equal(activity.TraceId.ToString(), envelope.Context!["activity_trace_id"]);
        Assert.Equal(activity.SpanId.ToString(), envelope.Context["activity_span_id"]);
        Assert.DoesNotContain("activity_trace_id", envelope.Correlation!.Keys);
    }

    [Fact]
    public async Task OptionalPrivacyFailurePreservesLatestAcceptedPrimaryWithLimits()
    {
        using var corpus = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures/evidence-wire-cases.json")));
        var payload = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(corpus.RootElement.GetProperty("events")[0].GetProperty("event").GetProperty("payload").GetRawText())!;
        var transport = new FakeTransport();
        using var client = DebugBundleClient.Create(new DebugBundleOptions
        {
            ProjectToken = "synthetic",
            ProjectMode = DebugBundleProjectMode.LocalOnly,
            Transport = transport,
            RedactFields = new[] { "classification" },
            BeforeSend = candidate => { candidate.EventType = "frontend_exception"; candidate.Payload = payload; candidate.Payload["message"] = "latest accepted redaction"; return candidate; }
        });
        client.CaptureMessage("private original", DebugBundleLogLevel.Error);
        await client.FlushAsync();
        var result = Assert.Single(transport.Batches.SelectMany(batch => batch));
        Assert.Equal("latest accepted redaction", result.Payload["message"]);
        Assert.DoesNotContain("exception_evidence", result.Payload.Keys);
        Assert.True(BeforeSendProcessor.IsValid(result));
        Assert.True(result.Context!.ContainsKey("evidence_limits"));
    }
}
