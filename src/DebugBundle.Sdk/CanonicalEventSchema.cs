using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DebugBundle;

// Closed Draft 7 interpreter for the packaged accepting contract. Only native
// owned JSON enters this boundary; quota or unavailable schema fails closed.
internal sealed class CanonicalEventSchema
{
    private static readonly HashSet<string> Keywords = new(new[] { "$schema", "$id", "$comment", "$ref", "type", "properties", "required", "additionalProperties", "items", "anyOf", "enum", "const", "minItems", "maxItems", "minLength", "maxLength", "minimum", "maximum", "exclusiveMinimum", "pattern", "format" }, StringComparer.Ordinal);
    private static readonly Regex Uuid = new("\\A[a-fA-F0-9]{8}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{12}\\z", RegexOptions.CultureInvariant);
    private static readonly Regex Timestamp = new("\\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\\.[0-9]+)?Z\\z", RegexOptions.CultureInvariant);
    private static readonly Lazy<CanonicalEventSchema?> Shared = new(Load);
    private readonly JsonElement _root;
    private readonly Dictionary<string, Regex> _patterns = new(StringComparer.Ordinal);
    private static CanonicalEventSchema? Load()
    {
        try
        {
            using var stream = typeof(CanonicalEventSchema).Assembly.GetManifestResourceStream("DebugBundle.EventEnvelopeSchema.json");
            if (stream == null) return null;
            using var document = JsonDocument.Parse(stream);
            return new CanonicalEventSchema(document.RootElement.Clone());
        }
        catch { return null; }
    }
    internal CanonicalEventSchema(JsonElement schema) { _root = schema.Clone(); Compile(_root, 0); }
    private void Compile(JsonElement schema, int depth)
    {
        if (depth > 128) throw new ArgumentException("invalid schema");
        if (schema.ValueKind is JsonValueKind.True or JsonValueKind.False) return;
        if (schema.ValueKind != JsonValueKind.Object) throw new ArgumentException("invalid schema");
        foreach (var field in schema.EnumerateObject())
        {
            if (!Keywords.Contains(field.Name)) throw new ArgumentException("unknown schema keyword");
            switch (field.Name)
            {
                case "properties": foreach (var child in field.Value.EnumerateObject()) Compile(child.Value, depth + 1); break;
                case "items": case "additionalProperties": Compile(field.Value, depth + 1); break;
                case "anyOf": foreach (var child in field.Value.EnumerateArray()) Compile(child, depth + 1); break;
                case "pattern": _patterns[field.Value.GetString()!] = new Regex(field.Value.GetString()!, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)); break;
                case "format": if (field.Value.GetString() is not ("uuid" or "date-time")) throw new ArgumentException("unknown format"); break;
                case "$ref": _ = Resolve(field.Value.GetString()!); break;
                case "type":
                    if (field.Value.ValueKind == JsonValueKind.Array) { foreach (var type in field.Value.EnumerateArray()) CheckType(type.GetString()); }
                    else CheckType(field.Value.GetString());
                    break;
            }
        }
    }
    private static void CheckType(string? type)
    {
        if (type is not ("object" or "array" or "string" or "number" or "integer" or "boolean" or "null")) throw new ArgumentException("unknown type");
    }
    private JsonElement Resolve(string path)
    {
        if (!path.StartsWith("#/", StringComparison.Ordinal)) throw new ArgumentException("nonlocal reference");
        var schema = _root;
        foreach (var part in path.Substring(2).Split('/'))
        {
            var key = part.Replace("~1", "/").Replace("~0", "~");
            if (schema.ValueKind == JsonValueKind.Array && int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var index)) schema = schema[index];
            else schema = schema.GetProperty(key);
        }
        return schema;
    }
    internal static bool Accepts(Dictionary<string, object?> owned)
    {
        var engine = Shared.Value ?? throw new EvidenceValidationUnavailableException();
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(owned, OwnedJson.Options));
        var remaining = 100000;
        return engine.Match(document.RootElement, engine._root, ref remaining, 0);
    }
    private bool Match(JsonElement value, JsonElement schema, ref int remaining, int depth)
    {
        if (--remaining < 0 || depth > 64) throw new EvidenceValidationUnavailableException();
        if (schema.ValueKind == JsonValueKind.True) return true;
        if (schema.ValueKind == JsonValueKind.False) return false;
        if (schema.TryGetProperty("$ref", out var reference)) return Match(value, Resolve(reference.GetString()!), ref remaining, depth + 1);
        if (schema.TryGetProperty("type", out var types))
        {
            var matches = types.ValueKind == JsonValueKind.Array
                ? types.EnumerateArray().Any(type => TypeMatches(value, type.GetString())) : TypeMatches(value, types.GetString());
            if (!matches) return false;
        }
        if (schema.TryGetProperty("const", out var constant) && !Same(value, constant)) return false;
        if (schema.TryGetProperty("enum", out var choices) && !choices.EnumerateArray().Any(choice => Same(value, choice))) return false;
        if (schema.TryGetProperty("anyOf", out var variants))
        {
            var found = false;
            foreach (var variant in variants.EnumerateArray()) if (Match(value, variant, ref remaining, depth + 1)) { found = true; break; }
            if (!found) return false;
        }
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                if (schema.TryGetProperty("required", out var required)) foreach (var key in required.EnumerateArray()) if (!value.TryGetProperty(key.GetString()!, out _)) return false;
                var hasProperties = schema.TryGetProperty("properties", out var properties);
                foreach (var item in value.EnumerateObject())
                {
                    if (hasProperties && properties.TryGetProperty(item.Name, out var property)) { if (!Match(item.Value, property, ref remaining, depth + 1)) return false; }
                    else if (schema.TryGetProperty("additionalProperties", out var additional) && !Match(item.Value, additional, ref remaining, depth + 1)) return false;
                }
                break;
            case JsonValueKind.Array:
                var length = value.GetArrayLength();
                if (!Within(length, schema, "minItems", "maxItems")) return false;
                if (schema.TryGetProperty("items", out var items)) foreach (var item in value.EnumerateArray()) if (!Match(item, items, ref remaining, depth + 1)) return false;
                break;
            case JsonValueKind.String:
                var text = value.GetString()!;
                if (!Within(text.Length, schema, "minLength", "maxLength")) return false;
                if (schema.TryGetProperty("pattern", out var pattern))
                {
                    var expression = pattern.GetString()!;
                    var match = _patterns[expression].Match(text);
                    if (!match.Success || expression.EndsWith("$", StringComparison.Ordinal) && match.Index + match.Length != text.Length) return false;
                }
                if (schema.TryGetProperty("format", out var format) && !(format.GetString() == "uuid" ? Uuid.IsMatch(text) : ValidTimestamp(text))) return false;
                break;
            case JsonValueKind.Number:
                var number = value.GetDouble();
                if (double.IsNaN(number) || double.IsInfinity(number) || !Within(number, schema, "minimum", "maximum")) return false;
                if (schema.TryGetProperty("exclusiveMinimum", out var lower) && number <= lower.GetDouble()) return false;
                break;
        }
        return true;
    }
    private static bool Within(double value, JsonElement schema, string minimum, string maximum) =>
        (!schema.TryGetProperty(minimum, out var lower) || value >= lower.GetDouble()) && (!schema.TryGetProperty(maximum, out var upper) || value <= upper.GetDouble());
    private static bool TypeMatches(JsonElement value, string? type) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "null" => value.ValueKind == JsonValueKind.Null,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && !double.IsNaN(number) && !double.IsInfinity(number) && Math.Truncate(number) == number,
        _ => false
    };
    private static bool Same(JsonElement left, JsonElement right) =>
        left.ValueKind == right.ValueKind && (left.ValueKind == JsonValueKind.Number ? left.GetDouble() == right.GetDouble() :
            left.ValueKind == JsonValueKind.String ? left.GetString() == right.GetString() : left.GetRawText() == right.GetRawText());
    private static bool ValidTimestamp(string value)
    {
        if (!Timestamp.IsMatch(value)) return false;
        int Number(int start, int length) => int.Parse(value.Substring(start, length), CultureInfo.InvariantCulture);
        var year = Number(0, 4); var month = Number(5, 2); var day = Number(8, 2);
        if (month is < 1 or > 12 || Number(11, 2) > 23 || Number(14, 2) > 59 || Number(17, 2) > 59) return false;
        var days = new[] { 31, year % 400 == 0 || year % 4 == 0 && year % 100 != 0 ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31 };
        return day >= 1 && day <= days[month - 1];
    }
}
