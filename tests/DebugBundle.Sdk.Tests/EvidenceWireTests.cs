using System.Text.Json;
using DebugBundle;

namespace DebugBundle.Sdk.Tests;

public sealed class EvidenceWireTests
{
    [Fact]
    public void CompleteCanonicalAndSemanticAcceptanceMatchesSharedCorpus()
    {
        using var corpus = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures/evidence-wire-cases.json")));
        foreach (var fixture in corpus.RootElement.GetProperty("events").EnumerateArray())
        {
            Assert.True(EvidenceWire.Accepts(fixture.GetProperty("event")) == fixture.GetProperty("valid").GetBoolean(), fixture.GetProperty("id").GetString());
        }
    }
    [Fact]
    public void ReceivingJsonByteCountMatchesSharedNativeNumberAndUnicodeVectors()
    {
        using var corpus = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures/evidence-wire-cases.json")));
        foreach (var fixture in corpus.RootElement.GetProperty("json_sizes").EnumerateArray())
            Assert.Equal(fixture.GetProperty("bytes").GetInt32(), AcceptingJsonSize.Size(fixture.GetProperty("value")));
    }

    [Fact]
    public void EquivalentNativeNumberFormsRetainCanonicalMeaning()
    {
        using var corpus = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures/evidence-wire-cases.json")));
        var source = JsonSerializer.Serialize(corpus.RootElement.GetProperty("events")[0].GetProperty("event"));
        source = source.Replace("\"version\":1", "\"version\":1.0").Replace("\"root_id\":0", "\"root_id\":0.0").Replace("\"id\":0", "\"id\":0.0");
        using var equivalent = JsonDocument.Parse(source);
        Assert.True(EvidenceWire.Accepts(equivalent.RootElement));
        using var negativeZero = JsonDocument.Parse("-0");
        Assert.Equal(1, AcceptingJsonSize.Size(negativeZero.RootElement));
    }
}
