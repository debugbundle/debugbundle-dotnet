using System.Collections;
using DebugBundle.Redaction;

namespace DebugBundle.Sdk.Tests;

public sealed class ContextProjectionTests
{
    [Fact]
    public async Task CustomContextEnumerationCountAndObjectGettersNeverRunOnCapture()
    {
        using var release = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        var custom = new CustomDictionary(release);
        var transport = new FakeTransport();
        using var client = DebugBundleClient.Create(new DebugBundleOptions
        {
            ProjectMode = DebugBundleProjectMode.LocalOnly,
            Transport = transport,
            BatchSize = 512,
            FlushInterval = TimeSpan.FromHours(1)
        });
        var capture = Task.Run(() =>
        {
            started.Set();
            client.SetContext("custom", custom);
            client.CaptureLog("context boundary", DebugBundleLogLevel.Error, new Dictionary<string, object?>
            {
                ["nested"] = custom,
                ["getter"] = new CustomObject(),
                ["safe"] = new List<object?> { "ok", new Dictionary<string, object?> { ["token"] = "private" } }
            });
        });
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        try { Assert.Same(capture, await Task.WhenAny(capture, Task.Delay(500))); }
        finally { release.Set(); }
        await capture;
        Assert.False(custom.Visited);
        Assert.Equal("[Unsupported value]", TelemetryPrivacy.Protect(custom));
        Assert.Equal("[Unsupported value]", new DebugBundleRedactor().Redact(custom));
        await client.FlushAsync();
        var envelope = Assert.Single(transport.Batches.SelectMany(batch => batch));
        var attributes = Assert.IsType<Dictionary<string, object?>>(envelope.Payload["attributes"]);
        Assert.Equal("[Unsupported value]", attributes["nested"]);
        Assert.Equal("[Unsupported value]", attributes["getter"]);
        var safe = Assert.IsType<List<object?>>(attributes["safe"]);
        Assert.Equal("ok", safe[0]);
        Assert.Equal("[REDACTED]", Assert.IsType<Dictionary<string, object?>>(safe[1])["token"]);
    }

    private sealed class CustomObject { public string Value => throw new InvalidOperationException("getter executed"); }
    private sealed class CustomDictionary(ManualResetEventSlim release) : Hashtable
    {
        internal bool Visited;
        public override int Count { get { Visited = true; release.Wait(); return base.Count; } }
        public override IDictionaryEnumerator GetEnumerator() { Visited = true; release.Wait(); return base.GetEnumerator(); }
    }
}
