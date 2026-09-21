using System.Collections;

namespace DebugBundle;

internal static class BeforeSendProcessor
{
    private static readonly IReadOnlyDictionary<string, string[]> RequiredPayloadFields =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["backend_exception"] = new[] { "name", "message", "stack", "handled", "request", "response", "runtime" },
            ["request_event"] = new[] { "method", "path", "query", "headers", "response_status", "duration_ms" },
            ["log_event"] = new[] { "level", "message", "attributes" },
            ["frontend_breadcrumb"] = new[] { "breadcrumb_type", "data" },
            ["frontend_exception"] = new[] { "name", "message", "stack" },
            ["deploy_metadata"] = new[] { "commit_sha", "version", "branch", "environment", "deployed_at" },
            ["error_suppressed"] = new[] { "fingerprint", "suppressed_count", "window_seconds", "first_seen", "last_seen" },
            ["probe_event"] = new[] { "label", "data", "activation_id", "probe_label_pattern" }
        };
    private static readonly IReadOnlyDictionary<string, HashSet<string>> AllowedPayloadFields =
        new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["backend_exception"] = Set("name", "message", "stack", "handled", "request", "response", "runtime", "probe_data"),
            ["request_event"] = Set(
                "method", "path", "query", "headers", "body", "response_status", "duration_ms",
                "route_template", "response_headers", "response_body", "device"),
            ["log_event"] = Set("level", "message", "attributes", "device"),
            ["frontend_breadcrumb"] = Set("breadcrumb_type", "route", "data", "device"),
            ["frontend_exception"] = Set(
                "name", "message", "stack", "route", "browser", "breadcrumbs", "device",
                "browser_event", "rejection_reason", "dom_context", "probe_data"),
            ["deploy_metadata"] = Set("commit_sha", "version", "branch", "environment", "deployed_at"),
            ["error_suppressed"] = Set(
                "fingerprint", "suppressed_count", "window_seconds", "first_seen", "last_seen", "device"),
            ["probe_event"] = Set("label", "data", "activation_id", "probe_label_pattern", "device")
        };

    public static DebugBundleEventEnvelope? Apply(
        DebugBundleEventEnvelope envelope,
        Func<DebugBundleEventEnvelope, DebugBundleEventEnvelope?>? hook)
    {
        if (hook == null)
        {
            return envelope;
        }

        try
        {
            var result = hook(Clone(envelope));
            if (result == null)
            {
                return null;
            }

            return IsValid(result) ? result : envelope;
        }
        catch
        {
            return envelope;
        }
    }

    private static DebugBundleEventEnvelope Clone(DebugBundleEventEnvelope envelope)
    {
        return new DebugBundleEventEnvelope
        {
            SchemaVersion = envelope.SchemaVersion,
            EventId = envelope.EventId,
            EventType = envelope.EventType,
            ProjectToken = envelope.ProjectToken,
            SdkName = envelope.SdkName,
            SdkVersion = envelope.SdkVersion,
            Service = new DebugBundleServiceDescriptor
            {
                Name = envelope.Service.Name,
                Runtime = envelope.Service.Runtime,
                Framework = envelope.Service.Framework,
                Environment = envelope.Service.Environment
            },
            OccurredAt = envelope.OccurredAt,
            Correlation = envelope.Correlation == null ? null : CloneDictionary(envelope.Correlation),
            Context = envelope.Context == null ? null : CloneDictionary(envelope.Context),
            Payload = CloneDictionary(envelope.Payload)
        };
    }

    private static Dictionary<string, object?> CloneDictionary(IReadOnlyDictionary<string, object?> source)
    {
        return source.ToDictionary(item => item.Key, item => CloneValue(item.Value), StringComparer.Ordinal);
    }

    private static object? CloneValue(object? value)
    {
        if (value is IReadOnlyDictionary<string, object?> readOnlyDictionary)
        {
            return CloneDictionary(readOnlyDictionary);
        }
        if (value is IDictionary<string, object?> dictionary)
        {
            return dictionary.ToDictionary(item => item.Key, item => CloneValue(item.Value), StringComparer.Ordinal);
        }
        if (value is IEnumerable enumerable and not string)
        {
            return enumerable.Cast<object?>().Select(CloneValue).ToList();
        }
        return value;
    }

    internal static bool IsValid(DebugBundleEventEnvelope envelope)
    {
        if (string.IsNullOrWhiteSpace(envelope.SchemaVersion) ||
            !Guid.TryParse(envelope.EventId, out _) ||
            string.IsNullOrWhiteSpace(envelope.EventType) ||
            string.IsNullOrWhiteSpace(envelope.SdkName) ||
            string.IsNullOrWhiteSpace(envelope.SdkVersion) ||
            !DateTimeOffset.TryParse(envelope.OccurredAt, out _) ||
            string.IsNullOrWhiteSpace(envelope.Service.Name) ||
            string.IsNullOrWhiteSpace(envelope.Service.Environment) ||
            envelope.Payload == null ||
            !RequiredPayloadFields.TryGetValue(envelope.EventType, out var requiredFields) ||
            !AllowedPayloadFields.TryGetValue(envelope.EventType, out var allowedFields))
        {
            return false;
        }

        return requiredFields.All(envelope.Payload.ContainsKey) &&
            envelope.Payload.Keys.All(allowedFields.Contains) &&
            HasValidPayloadShape(envelope.EventType, envelope.Payload);
    }

    private static bool HasValidPayloadShape(
        string eventType,
        IReadOnlyDictionary<string, object?> payload)
    {
        return eventType switch
        {
            "backend_exception" =>
                HasNonEmptyStrings(payload, "name", "message", "stack") &&
                payload["handled"] is bool &&
                IsDictionary(payload["request"]) &&
                IsDictionary(payload["response"]) &&
                IsDictionary(payload["runtime"]) &&
                IsOptionalDictionary(payload, "probe_data"),
            "request_event" =>
                HasNonEmptyStrings(payload, "method", "path") &&
                IsDictionary(payload["query"]) &&
                IsDictionary(payload["headers"]) &&
                IsNonNegativeNumber(payload["response_status"]) &&
                IsNonNegativeNumber(payload["duration_ms"]) &&
                IsOptionalDictionary(payload, "response_headers"),
            "log_event" =>
                HasNonEmptyStrings(payload, "level", "message") &&
                IsDictionary(payload["attributes"]),
            "frontend_breadcrumb" =>
                HasNonEmptyStrings(payload, "breadcrumb_type") &&
                IsDictionary(payload["data"]),
            "frontend_exception" =>
                HasNonEmptyStrings(payload, "name", "message", "stack") &&
                (!payload.ContainsKey("breadcrumbs") || IsList(payload["breadcrumbs"])) &&
                IsOptionalDictionary(payload, "probe_data"),
            "deploy_metadata" =>
                HasNonEmptyStrings(payload, "commit_sha", "version", "branch", "environment") &&
                IsTimestamp(payload["deployed_at"]),
            "error_suppressed" =>
                HasNonEmptyStrings(payload, "fingerprint") &&
                IsNonNegativeInteger(payload["suppressed_count"]) &&
                IsPositiveInteger(payload["window_seconds"]) &&
                IsTimestamp(payload["first_seen"]) &&
                IsTimestamp(payload["last_seen"]),
            "probe_event" =>
                HasNonEmptyStrings(payload, "label", "probe_label_pattern") &&
                IsDictionary(payload["data"]) &&
                IsNullableGuid(payload["activation_id"]),
            _ => false
        };
    }

    private static HashSet<string> Set(params string[] values)
    {
        return new HashSet<string>(values, StringComparer.Ordinal);
    }

    private static bool HasNonEmptyStrings(
        IReadOnlyDictionary<string, object?> payload,
        params string[] fields)
    {
        return fields.All(field =>
            payload.TryGetValue(field, out var value) &&
            value is string text &&
            !string.IsNullOrWhiteSpace(text));
    }

    private static bool IsDictionary(object? value)
    {
        return value is IReadOnlyDictionary<string, object?> ||
            value is IDictionary<string, object?>;
    }

    private static bool IsOptionalDictionary(
        IReadOnlyDictionary<string, object?> payload,
        string field)
    {
        return !payload.TryGetValue(field, out var value) || IsDictionary(value);
    }

    private static bool IsList(object? value)
    {
        return value is IEnumerable and not string &&
            value is not IDictionary;
    }

    private static bool IsNonNegativeNumber(object? value)
    {
        return TryFiniteNumber(value, out var number) && number >= 0;
    }

    private static bool IsNonNegativeInteger(object? value)
    {
        return TryFiniteNumber(value, out var number) &&
            number >= 0 &&
            Math.Truncate(number) == number;
    }

    private static bool IsPositiveInteger(object? value)
    {
        return TryFiniteNumber(value, out var number) &&
            number > 0 &&
            Math.Truncate(number) == number;
    }

    private static bool TryFiniteNumber(object? value, out double number)
    {
        if (value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
        {
            number = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
            return !double.IsNaN(number) && !double.IsInfinity(number);
        }
        number = 0;
        return false;
    }

    private static bool IsTimestamp(object? value)
    {
        return value is string timestamp && DateTimeOffset.TryParse(timestamp, out _);
    }

    private static bool IsNullableGuid(object? value)
    {
        return value == null || value is string uuid && Guid.TryParse(uuid, out _);
    }
}
