using System.Text.Json;
using DebugBundle;

namespace DebugBundle.Sdk.Tests;

public sealed class EvidenceCapabilitiesTests
{
    [Fact]
    public void ClosedProofDescriptorMatchesSharedCorpusAndCannotBeMutatedThroughOptions()
    {
        using var corpus = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures/evidence-wire-cases.json")));
        foreach (var fixture in corpus.RootElement.GetProperty("capabilities").EnumerateArray())
        {
            var expected = fixture.GetProperty("features").EnumerateArray().Select(item => item.GetString()).ToArray();
            Assert.Equal(expected, EvidenceDeliveryState.Normalize(fixture.GetProperty("input")).Features);
        }
        var pin = new EvidenceCapabilities { Version = 1, Features = new[] { "exception_evidence_v1" } };
        var options = new DebugBundleOptions { EvidenceCapabilities = pin, CaptureExceptionEvidence = false }.Resolve();
        pin.Features[0] = "changed";
        Assert.False(options.CaptureExceptionEvidence);
        Assert.Equal("exception_evidence_v1", Assert.Single(options.EvidenceCapabilities.Features));
    }
    [Fact]
    public async Task NativeExceptionCollectionRequiresLocalFlagAndCompletePathProof()
    {
        foreach (var mode in new[] { "unknown", "disabled", "enabled" })
        {
            var transport = new FakeTransport();
            using var client = DebugBundleClient.Create(new DebugBundleOptions
            {
                ProjectMode = DebugBundleProjectMode.LocalOnly,
                Transport = transport,
                CaptureExceptionEvidence = mode != "disabled",
                EvidenceCapabilities = mode == "unknown" ? null : new EvidenceCapabilities { Version = 1, Features = new[] { "exception_evidence_v1" } }
            });
            var exception = new InvalidOperationException("fictional root", new ArgumentException("fictional cause"));
            try { throw exception; } catch (Exception error) { client.CaptureException(error); }
            await client.FlushAsync();
            GC.KeepAlive(exception);
            var envelope = Assert.Single(transport.Batches.SelectMany(batch => batch));
            Assert.Equal(mode == "enabled", envelope.Payload.ContainsKey("exception_evidence"));
            Assert.True(BeforeSendProcessor.IsValid(envelope));
        }
    }
}
