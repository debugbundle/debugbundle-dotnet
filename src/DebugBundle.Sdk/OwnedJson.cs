using System.Collections;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DebugBundle;

internal sealed class EvidenceValidationUnavailableException : Exception { }

// Native containers only: application enumerators, serializers and property getters
// are never invoked while deciding whether a hook result can be retained safely.
internal static class OwnedJson
{
    private const int MaxBytes = 8 * 1024 * 1024;
    internal static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, MaxDepth = 64 };
    private sealed class ReferenceComparer : IEqualityComparer<object>
    {
        internal static readonly ReferenceComparer Instance = new();
        public new bool Equals(object? left, object? right) => ReferenceEquals(left, right);
        public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
    }
    private sealed class Budget
    {
        internal int Nodes = 4096, Bytes = MaxBytes;
        internal readonly HashSet<object> Active = new(ReferenceComparer.Instance);
    }
    internal static Dictionary<string, object?> Snapshot(DebugBundleEventEnvelope envelope)
    {
        if (envelope.Service == null || envelope.Payload == null) throw new ArgumentException("invalid event");
        var service = new Dictionary<string, object?>
        {
            ["name"] = envelope.Service.Name,
            ["runtime"] = envelope.Service.Runtime,
            ["environment"] = envelope.Service.Environment
        };
        if (envelope.Service.Framework != null) service["framework"] = envelope.Service.Framework;
        var input = new Dictionary<string, object?>
        {
            ["schema_version"] = envelope.SchemaVersion,
            ["event_id"] = envelope.EventId,
            ["event_type"] = envelope.EventType,
            ["sdk_name"] = envelope.SdkName,
            ["sdk_version"] = envelope.SdkVersion,
            ["occurred_at"] = envelope.OccurredAt,
            ["service"] = service,
            ["payload"] = envelope.Payload
        };
        if (envelope.ProjectToken != null) input["project_token"] = envelope.ProjectToken;
        if (envelope.Context != null) input["context"] = envelope.Context;
        if (envelope.Correlation != null) input["correlation"] = envelope.Correlation;
        var owned = (Dictionary<string, object?>)Copy(input, new Budget(), 0)!;
        if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(owned, Options)) > MaxBytes) throw new EvidenceValidationUnavailableException();
        return owned;
    }
    internal static DebugBundleEventEnvelope Event(Dictionary<string, object?> owned)
    {
        var envelope = JsonSerializer.Deserialize<DebugBundleEventEnvelope>(JsonSerializer.Serialize(owned, Options))
            ?? throw new ArgumentException("invalid event");
        envelope.Payload = (Dictionary<string, object?>)owned["payload"]!;
        envelope.Context = owned.TryGetValue("context", out var context) ? (Dictionary<string, object?>?)context : null;
        envelope.Correlation = owned.TryGetValue("correlation", out var correlation) ? (Dictionary<string, object?>?)correlation : null;
        return envelope;
    }
    private static object? Copy(object? value, Budget budget, int depth)
    {
        if (--budget.Nodes < 0 || depth > 16 || budget.Bytes < 0) throw new EvidenceValidationUnavailableException();
        if (value == null || value is bool) return value;
        if (value is string text) { budget.Bytes -= Encoding.UTF8.GetByteCount(text); if (budget.Bytes < 0) throw new EvidenceValidationUnavailableException(); return text; }
        if (value is JsonElement element) return CopyJson(element, budget, depth);
        if (value is byte or sbyte or short or ushort or int or uint or long or ulong or decimal) return value;
        if (value is double number) { if (double.IsNaN(number) || double.IsInfinity(number)) throw new ArgumentException("invalid number"); return value; }
        if (value is float fraction) { if (float.IsNaN(fraction) || float.IsInfinity(fraction)) throw new ArgumentException("invalid number"); return value; }
        if (!Redaction.TelemetryPrivacy.IsSafeContainer(value)) throw new EvidenceValidationUnavailableException();
        if (!budget.Active.Add(value)) throw new EvidenceValidationUnavailableException();
        try
        {
            if (value is IDictionary dictionary)
            {
                if (dictionary.Count > budget.Nodes / 2) throw new EvidenceValidationUnavailableException();
                var output = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (DictionaryEntry item in dictionary)
                {
                    if (item.Key is not string key) throw new ArgumentException("invalid key");
                    _ = Copy(key, budget, depth + 1);
                    output[key] = Copy(item.Value, budget, depth + 1);
                }
                return output;
            }
            if (value is IList list)
            {
                if (list.Count > budget.Nodes) throw new EvidenceValidationUnavailableException();
                var output = new List<object?>();
                for (var index = 0; index < list.Count; index++) output.Add(Copy(list[index], budget, depth + 1));
                return output;
            }
            throw new EvidenceValidationUnavailableException();
        }
        finally { budget.Active.Remove(value); }
    }
    private static object? CopyJson(JsonElement element, Budget budget, int depth)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var output = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject()) { _ = Copy(property.Name, budget, depth + 1); output[property.Name] = Copy(property.Value, budget, depth + 1); }
                return output;
            case JsonValueKind.Array:
                var items = new List<object?>();
                foreach (var item in element.EnumerateArray()) items.Add(Copy(item, budget, depth + 1));
                return items;
            case JsonValueKind.String: return Copy(element.GetString(), budget, depth);
            case JsonValueKind.Number:
                if (element.TryGetInt64(out var integer)) return integer >= int.MinValue && integer <= int.MaxValue ? (object)(int)integer : integer;
                return Copy(element.GetDouble(), budget, depth);
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.Null: return null;
            default: throw new ArgumentException("invalid JSON");
        }
    }
}
