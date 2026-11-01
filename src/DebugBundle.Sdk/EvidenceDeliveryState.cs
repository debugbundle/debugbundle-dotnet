using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DebugBundle;

/// <summary>A pin asserts support for the complete delivery and reader path.</summary>
public sealed class EvidenceCapabilities
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;
    [JsonPropertyName("features")]
    public string[] Features { get; set; } = Array.Empty<string>();
}

internal sealed class EvidenceDeliveryState
{
    internal static readonly string[] Known = { "exception_evidence_v1", "resource_observations_v1", "recovery_v1", "evidence_provenance_v1" };
    private readonly object _sync = new();
    private EvidenceCapabilities _proof;
    internal EvidenceDeliveryState(EvidenceCapabilities? pin) { _proof = Normalize(pin); }
    internal static EvidenceCapabilities Normalize(EvidenceCapabilities? value)
    {
        if (value == null || value.Version != 1 || value.Features == null || value.Features.Length > 4) return new();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var feature in value.Features) if (Array.IndexOf(Known, feature) < 0 || !seen.Add(feature)) return new();
        return new() { Features = Known.Where(seen.Contains).ToArray() };
    }
    internal static EvidenceCapabilities Normalize(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != 2 ||
            !value.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || version.GetDouble() != 1 ||
            !value.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array || features.GetArrayLength() > 4) return new();
        var values = new List<string>();
        foreach (var feature in features.EnumerateArray()) { if (feature.ValueKind != JsonValueKind.String) return new(); values.Add(feature.GetString()!); }
        return Normalize(new EvidenceCapabilities { Features = values.ToArray() });
    }
    internal EvidenceCapabilities Descriptor() { lock (_sync) return Normalize(_proof); }
    internal bool Supports(string feature) { lock (_sync) return Array.IndexOf(_proof.Features, feature) >= 0; }
    internal bool Observe(JsonElement? value)
    {
        var next = value.HasValue ? Normalize(value.Value) : new EvidenceCapabilities();
        lock (_sync)
        {
            var lost = _proof.Features.Any(feature => Array.IndexOf(next.Features, feature) < 0);
            _proof = next; return lost;
        }
    }
    internal bool ObserveBody(string? body, bool required, bool validAcknowledgement)
    {
        if (body == null || body.Length > 262144 || Encoding.UTF8.GetByteCount(body) > 262144) return required && Observe(null);
        try
        {
            using var document = JsonDocument.Parse(body);
            var found = document.RootElement.TryGetProperty("evidence_capabilities", out var descriptor);
            if (!required && !found) return false;
            return Observe(found && validAcknowledgement ? descriptor : (JsonElement?)null);
        }
        catch { return required && Observe(null); }
    }
    internal static EvidenceCapabilities Intersect(IReadOnlyList<EvidenceCapabilities> hops)
    {
        if (hops.Count == 0 || hops.Count > 8) return new();
        var remaining = new HashSet<string>(Known, StringComparer.Ordinal);
        foreach (var hop in hops) remaining.IntersectWith(Normalize(hop).Features);
        return new() { Features = Known.Where(remaining.Contains).ToArray() };
    }
}
