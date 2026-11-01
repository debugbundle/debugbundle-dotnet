using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DebugBundle;

internal static class EvidenceWire
{
    private static readonly Regex Fresh = new("\\A[a-fA-F0-9]{8}-[a-fA-F0-9]{4}-4[a-fA-F0-9]{3}-[89abAB][a-fA-F0-9]{3}-[a-fA-F0-9]{12}\\z", RegexOptions.CultureInvariant);
    internal static bool FreshId(string? value) => value != null && value.Length == 36 && Fresh.IsMatch(value);
    internal static bool Accepts(JsonElement value)
    {
        try
        {
            // Preserve the full supplied object: typed deserialization alone would
            // discard unrecognised envelope fields before closed-schema checking.
            var owned = JsonSerializer.Deserialize<Dictionary<string, object?>>(value.GetRawText())!;
            return CanonicalEventSchema.Accepts(owned) && Semantic(value);
        }
        catch (EvidenceValidationUnavailableException) { throw; }
        catch { return false; }
    }
    internal static bool Accepts(Dictionary<string, object?> owned)
    {
        if (!CanonicalEventSchema.Accepts(owned)) return false;
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(owned, OwnedJson.Options));
        return Semantic(document.RootElement);
    }
    private static bool Id(JsonElement value) => value.ValueKind == JsonValueKind.String && Fresh.IsMatch(value.GetString()!);
    internal static bool Scope(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != 2 || !value.TryGetProperty("kind", out var kind)) return false;
        var field = kind.GetString() == "page" ? "page_id" : kind.GetString() == "operation" ? "operation_id" : "";
        return field.Length > 0 && value.TryGetProperty(field, out var id) && Id(id);
    }
    internal static bool Reference(JsonElement value) => value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Count() == 3 &&
        value.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.Number && version.GetDouble() == 1 &&
        value.TryGetProperty("failure_ref", out var failure) && Id(failure) && value.TryGetProperty("scope", out var scope) && Scope(scope);
    private static bool Text(JsonElement value, int maximum) => value.ValueKind == JsonValueKind.Null ||
        value.ValueKind == JsonValueKind.String && Encoding.UTF8.GetByteCount(value.GetString()!) <= maximum;
    private static bool Semantic(JsonElement value)
    {
        var payload = value.GetProperty("payload"); var type = value.GetProperty("event_type").GetString();
        if (type == "frontend_exception")
        {
            var name = value.GetProperty("sdk_name").GetString();
            var mobile = name is "@debugbundle/sdk-android" or "@debugbundle/sdk-swift" or "@debugbundle/sdk-react-native";
            if (!payload.TryGetProperty(mobile ? "device" : "browser", out var device) || device.ValueKind != JsonValueKind.Object) return false;
        }
        if (type == "diagnostic_context")
        {
            if (!Id(payload.GetProperty("failure_ref")) || !Scope(payload.GetProperty("scope"))) return false;
            if (payload.GetProperty("kind").GetString() == "recovery")
            {
                if (!Id(payload.GetProperty("attempt_id")) || payload.TryGetProperty("operation", out var operation) && !Text(operation, 64)) return false;
            }
            else if (!Resources(payload.GetProperty("observations"))) return false;
            if (payload.GetProperty("scope").GetProperty("kind").GetString() == "page")
            {
                if (!value.TryGetProperty("correlation", out var correlation) || !correlation.TryGetProperty("session_id", out var session) || session.ValueKind != JsonValueKind.String ||
                    session.GetString()!.All(letter => letter == '\uFEFF' || char.IsWhiteSpace(letter) && letter != '\u0085')) return false;
            }
            return true;
        }
        var additions = new Dictionary<string, object?>();
        foreach (var field in new[] { "exception_evidence", "execution", "resource_observations" })
        {
            if (!payload.TryGetProperty(field, out var item)) continue;
            if (!(type == "backend_exception" && field != "resource_observations" || type == "frontend_exception" && field != "execution")) return false;
            if (field == "exception_evidence" && !Graph(item) || field == "execution" && !Text(item.GetProperty("name"), 128) || field == "resource_observations" && !Resources(item)) return false;
            additions[field] = item;
        }
        if (additions.Count == 0) return true;
        if (value.TryGetProperty("context", out var context) && context.TryGetProperty("evidence_reference", out var reference) && Reference(reference)) additions["evidence_reference"] = reference;
        return AcceptingJsonSize.Size(additions) <= 32768;
    }
    private static bool Graph(JsonElement graph)
    {
        var nodes = graph.GetProperty("nodes").EnumerateArray().ToArray();
        var adjacency = new Dictionary<int, List<int>>();
        foreach (var node in nodes)
        {
            var id = (int)node.GetProperty("id").GetDouble(); if (adjacency.ContainsKey(id)) return false;
            adjacency[id] = new List<int>();
            if (!Text(node.GetProperty("name"), 256) || !Text(node.GetProperty("message"), 2048) || !Text(node.GetProperty("stack"), 16384)) return false;
            foreach (var frame in node.GetProperty("frames").EnumerateArray()) if (!Text(frame.GetProperty("function"), 256) || !Text(frame.GetProperty("file"), 2048)) return false;
        }
        foreach (var node in nodes)
        {
            var edges = new HashSet<string>(StringComparer.Ordinal); var causal = 0;
            foreach (var edge in node.GetProperty("relationships").EnumerateArray())
            {
                var target = (int)edge.GetProperty("target_id").GetDouble(); var kind = edge.GetProperty("kind").GetString();
                if (!adjacency.ContainsKey(target) || !edges.Add(kind + ":" + target)) return false;
                if (kind is "cause" or "context") causal++;
                adjacency[(int)node.GetProperty("id").GetDouble()].Add(target);
            }
            if (causal > 1) return false;
        }
        var active = new HashSet<int>(); var reached = new HashSet<int>();
        bool Visit(int id, int depth)
        {
            if (depth > 4 || !adjacency.TryGetValue(id, out var children) || !active.Add(id)) return false;
            reached.Add(id);
            foreach (var child in children) if (!Visit(child, depth + 1)) return false;
            active.Remove(id); return true;
        }
        return Visit((int)graph.GetProperty("root_id").GetDouble(), 0) && reached.Count == nodes.Length && AcceptingJsonSize.Size(graph) <= 32768;
    }
    private static bool Resources(JsonElement value)
    {
        foreach (var observation in value.GetProperty("items").EnumerateArray())
            if (observation.GetProperty("source_url") is var source && source.ValueKind != JsonValueKind.Null && !ResourceUrl(source.GetString()!)) return false;
        return AcceptingJsonSize.Size(value) <= 32768;
    }
    private static bool ResourceUrl(string value)
    {
        if (value.Length == 0 || Encoding.UTF8.GetByteCount(value) > 2048 || value.StartsWith("//", StringComparison.Ordinal) || value.Any(letter => letter <= 32 || letter == 127 || letter is '\\' or '?' or '#')) return false;
        if (value.StartsWith("/", StringComparison.Ordinal)) return true;
        var start = value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? 8 : value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? 7 : 0;
        if (start == 0) return false;
        var authority = value.Substring(start).Split('/')[0];
        return Uri.TryCreate(value.Substring(0, start) + authority + "/", UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host) && string.IsNullOrEmpty(uri.UserInfo);
    }
}
