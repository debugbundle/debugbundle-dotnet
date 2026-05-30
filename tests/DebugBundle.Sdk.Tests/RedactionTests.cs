using DebugBundle;
using DebugBundle.Redaction;

namespace DebugBundle.Sdk.Tests;

public sealed class RedactionTests
{
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
