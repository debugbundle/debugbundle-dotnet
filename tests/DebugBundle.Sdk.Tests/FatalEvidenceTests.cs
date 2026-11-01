using DebugBundle;

namespace DebugBundle.Sdk.Tests;

public sealed class FatalEvidenceTests
{
    private sealed class HostileException : Exception
    {
        internal int Calls;
        public override string Message { get { Interlocked.Increment(ref Calls); throw new InvalidOperationException(); } }
        public override string StackTrace { get { Interlocked.Increment(ref Calls); throw new InvalidOperationException(); } }
        public override string ToString() { Interlocked.Increment(ref Calls); throw new InvalidOperationException(); }
    }
    [Fact]
    public async Task FatalCaptureBypassesHostCallbacksAndBoundsCombinedEvidence()
    {
        var transport = new FakeTransport(); var hooks = 0; var random = 0;
        using var client = DebugBundleClient.Create(new DebugBundleOptions { ProjectMode = DebugBundleProjectMode.LocalOnly, Transport = transport, SampleRate = 0, RandomSource = () => { random++; throw new Exception(); }, BeforeSend = value => { hooks++; return null; }, EvidenceCapabilities = new EvidenceCapabilities { Features = new[] { "exception_evidence_v1", "recovery_v1" } }, CaptureRecoveryObservations = true });
        var hostile = new HostileException();
        client.RunWithOperationContext(new DebugBundleOperationOptions { Kind = "job", Name = new string('x', 128) }, () => client.CaptureFatal(hostile, EvidenceContext.ForFailure(Guid.NewGuid().ToString("D"))));
        await client.FlushAsync(); var captured = Assert.Single(Assert.Single(transport.Batches));
        Assert.False(Assert.IsType<bool>(captured.Payload["handled"])); Assert.Equal(0, hooks); Assert.Equal(0, random); Assert.Equal(0, hostile.Calls); Assert.True(BeforeSendProcessor.IsValid(captured));
        var graph = Assert.IsType<Dictionary<string, object?>>(captured.Payload["exception_evidence"]); var nodes = Assert.IsType<List<object?>>(graph["nodes"]); Assert.InRange(nodes.Count, 1, 2);
        Assert.InRange(nodes.Cast<Dictionary<string, object?>>().Sum(node => ((List<object?>)node["frames"]!).Count), 0, 8);
        var additions = captured.Payload.Where(item => item.Key is "exception_evidence" or "execution").ToDictionary(item => item.Key, item => item.Value);
        additions["evidence_reference"] = captured.Context!["evidence_reference"]; Assert.InRange(AcceptingJsonSize.Size(additions), 1, 4096);
    }
}
