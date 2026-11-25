using System.Collections;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DebugBundle.Redaction;

public static partial class TelemetryPrivacy
{
    private const long MaxSafeInteger = 9007199254740991L;

    private static readonly HashSet<string> AllowedHtmlResourceTags = new(StringComparer.Ordinal)
    {
        "script", "link", "img", "audio", "video", "source", "iframe", "object", "embed", "input"
    };
    private static readonly HashSet<string> AllowedReadyStates = new(StringComparer.Ordinal)
    {
        "loading", "interactive", "complete"
    };
    private static readonly HashSet<string> AllowedVisibilityStates = new(StringComparer.Ordinal)
    {
        "visible", "hidden", "prerender", "unloaded"
    };
    private static readonly HashSet<string> AllowedRejectionKinds = new(StringComparer.Ordinal)
    {
        "error", "string", "object", "null", "undefined", "unknown"
    };
    private static readonly HashSet<string> AllowedEvidenceLimitReasons = new(StringComparer.Ordinal)
    {
        "budget_exceeded", "unsafe", "unsupported", "unknown"
    };
    private static readonly HashSet<string> AllowedEvidenceLimitKeys = new(StringComparer.Ordinal)
    {
        "version", "exception_evidence", "execution", "resource_observations"
    };
    private static readonly HashSet<string> AllowedBrowserEventKeys = new(StringComparer.Ordinal)
    {
        "kind", "message", "file_name", "line_number", "column_number", "target", "page", "opaque"
    };
    private static readonly HashSet<string> AllowedTargetKeys = new(StringComparer.Ordinal)
    {
        "tag_name", "source_url", "attributes"
    };
    private static readonly string[] AllowedTargetAttrStringKeys = { "rel", "as", "type", "media", "cross_origin" };
    private static readonly string[] AllowedTargetAttrBoolKeys = { "async", "defer", "integrity_present" };
    private static readonly HashSet<string> AllowedTargetAttrKeys = new(AllowedTargetAttrStringKeys.Concat(AllowedTargetAttrBoolKeys), StringComparer.Ordinal);
    private static readonly HashSet<string> AllowedPageKeys = new(StringComparer.Ordinal)
    {
        "url", "referrer", "ready_state", "visibility_state"
    };
    private static readonly HashSet<string> AllowedRejectionKeys = new(StringComparer.Ordinal)
    {
        "kind", "name", "message", "preview"
    };
    private static readonly HashSet<string> AllowedExceptionRuntimes = new(StringComparer.Ordinal)
    {
        "node", "browser", "python", "php", "jvm", "go", "ruby", "dotnet", "android", "swift", "react_native", "unknown"
    };
    private static readonly HashSet<string> AllowedTraceOrigins = new(StringComparer.Ordinal)
    {
        "throw", "native_fatal", "capture", "runtime_reported", "unavailable", "unknown"
    };
    private static readonly HashSet<string> AllowedParsingStatuses = new(StringComparer.Ordinal)
    {
        "parsed", "partial", "unparsed", "unavailable", "truncated"
    };
    private static readonly HashSet<string> AllowedFrameClassifications = new(StringComparer.Ordinal)
    {
        "application", "framework", "sdk", "unknown"
    };
    private static readonly HashSet<string> AllowedRelationshipKinds = new(StringComparer.Ordinal)
    {
        "cause", "context", "aggregate", "suppressed"
    };
    private static readonly HashSet<string> AllowedExecutionKinds = new(StringComparer.Ordinal)
    {
        "request", "command", "job", "unknown"
    };
    private static readonly HashSet<string> AllowedRecoveryStates = new(StringComparer.Ordinal)
    {
        "attempt_started", "attempt_completed", "attempt_failed", "recovery_observed", "failure_persisted"
    };
    private static readonly HashSet<string> AllowedResourceAvailabilities = new(StringComparer.Ordinal)
    {
        "observed", "unsupported", "no_match", "withheld", "ambiguous", "unknown"
    };
    private static readonly HashSet<string> AllowedCspDispositions = new(StringComparer.Ordinal)
    {
        "enforce", "report"
    };
    private static readonly HashSet<string> AllowedGraphKeys = new(StringComparer.Ordinal)
    {
        "version", "runtime", "root_id", "nodes", "truncated", "unavailable_reason"
    };
    private static readonly HashSet<string> AllowedNodeKeys = new(StringComparer.Ordinal)
    {
        "id", "name", "message", "stack", "trace_origin", "parsing_status", "frames", "relationships"
    };
    private static readonly HashSet<string> AllowedFrameKeys = new(StringComparer.Ordinal)
    {
        "function", "file", "line", "column", "classification"
    };
    private static readonly HashSet<string> AllowedRelationshipKeys = new(StringComparer.Ordinal)
    {
        "kind", "target_id"
    };
    private static readonly HashSet<string> AllowedExecutionKeys = new(StringComparer.Ordinal)
    {
        "version", "kind", "name", "elapsed_ms", "limit_ms"
    };
    private static readonly HashSet<string> AllowedRecoveryKeys = new(StringComparer.Ordinal)
    {
        "version", "kind", "failure_ref", "scope", "attempt_id", "state", "operation", "status_code", "duration_ms"
    };
    private static readonly HashSet<string> AllowedResourceDiagnosticKeys = new(StringComparer.Ordinal)
    {
        "version", "kind", "failure_ref", "scope", "source_event_id", "observations"
    };
    private static readonly HashSet<string> AllowedResourceBlockKeys = new(StringComparer.Ordinal)
    {
        "version", "items"
    };
    private static readonly HashSet<string> AllowedTimingItemKeys = new(StringComparer.Ordinal)
    {
        "kind", "observed_at", "source_url", "availability", "response_status", "duration_ms", "transfer_size_bytes"
    };
    private static readonly HashSet<string> AllowedCspItemKeys = new(StringComparer.Ordinal)
    {
        "kind", "observed_at", "source_url", "effective_directive", "disposition", "document_status_code"
    };
    private static readonly HashSet<string> AllowedReferenceKeys = new(StringComparer.Ordinal)
    {
        "version", "failure_ref", "scope"
    };
    private static readonly Regex CspDirectivePattern = new("\\A[a-z-]{1,64}\\z", RegexOptions.CultureInvariant, MatchTimeout);
    private static readonly Regex StackUrlPattern = new("https?://[^\\s]+", IgnoreCase, MatchTimeout);
    private static readonly Regex StackCoordinateSuffix = new(":\\d+(?::\\d+)?[),]*$", RegexOptions.CultureInvariant, MatchTimeout);
    private static readonly Regex StackPunctuationSuffix = new("[),]+$", RegexOptions.CultureInvariant, MatchTimeout);

    private enum OwnedStatus
    {
        Ok,
        Unsafe
    }

    private sealed class OwnedBudget
    {
        public int Nodes;
        public int Bytes;
        public readonly HashSet<object> Seen = new(ReferenceComparer.Instance);
    }

    public static DebugBundleEventEnvelope? ProtectEvent(DebugBundleEventEnvelope eventEnvelope, IEnumerable<string>? additionalFields = null)
        => ProtectEvent(eventEnvelope, additionalFields, exactFields: null);

    /// <summary>Preserve validated closed SDK protocol structures while protecting captured application data.</summary>
    public static DebugBundleEventEnvelope? ProtectEvent(
        DebugBundleEventEnvelope eventEnvelope,
        IEnumerable<string>? additionalFields,
        IEnumerable<string>? exactFields)
    {
        try
        {
            if (eventEnvelope.Service == null || eventEnvelope.Payload == null || !IsSafeContainer(eventEnvelope.Payload))
            {
                return null;
            }
            if (eventEnvelope.Context != null && !IsSafeContainer(eventEnvelope.Context))
            {
                return null;
            }
            if (eventEnvelope.Correlation != null && (!IsSafeContainer(eventEnvelope.Correlation) || eventEnvelope.Correlation.Count > 8))
            {
                return null;
            }

            var identities = new object?[] { eventEnvelope.SchemaVersion, eventEnvelope.SdkName, eventEnvelope.SdkVersion }
                .Concat(eventEnvelope.Correlation?.Values ?? Enumerable.Empty<object?>());
            foreach (var value in identities)
            {
                if (value is not null && (value is not string scalar || !Equals(Protect(scalar, additionalFields, exactFields), scalar)))
                {
                    return null;
                }
            }

            var rawPayload = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var item in eventEnvelope.Payload)
            {
                rawPayload[item.Key] = item.Value;
            }

            var preservedPayload = new Dictionary<string, object?>(StringComparer.Ordinal);
            Dictionary<string, object?>? diagnosticPayload = null;

            if (eventEnvelope.EventType == "diagnostic_context")
            {
                if (TryNormalizeOwned(rawPayload, out var ownedDiagnostic) != OwnedStatus.Ok ||
                    ownedDiagnostic is not Dictionary<string, object?> diagnosticMap)
                {
                    return null;
                }
                diagnosticPayload = SanitizeDiagnosticContextPayload(diagnosticMap, additionalFields, exactFields);
                if (diagnosticPayload == null)
                {
                    return null;
                }
            }
            else
            {
                if (eventEnvelope.EventType == "frontend_exception")
                {
                    if (!TryPreserveBlock(rawPayload, "browser_event", preservedPayload,
                        map => SanitizeBrowserEventBlock(map, additionalFields, exactFields)))
                    {
                        return null;
                    }
                    if (!TryPreserveBlock(rawPayload, "rejection_reason", preservedPayload,
                        map => SanitizeRejectionReasonBlock(map, additionalFields, exactFields)))
                    {
                        return null;
                    }
                    if (!TryPreserveBlock(rawPayload, "resource_observations", preservedPayload,
                        map => SanitizeResourceObservationsBlock(map, additionalFields, exactFields)))
                    {
                        return null;
                    }
                }
                if (eventEnvelope.EventType is "backend_exception" or "frontend_exception")
                {
                    if (!TryPreserveBlock(rawPayload, "exception_evidence", preservedPayload,
                        map => SanitizeExceptionEvidenceBlock(map, additionalFields, exactFields)))
                    {
                        return null;
                    }
                }
                if (eventEnvelope.EventType == "backend_exception")
                {
                    if (!TryPreserveBlock(rawPayload, "execution", preservedPayload,
                        map => SanitizeExecutionBlock(map, additionalFields, exactFields)))
                    {
                        return null;
                    }
                }
                if (eventEnvelope.EventType == "error_suppressed" &&
                    rawPayload.TryGetValue("reason", out var rawReason) &&
                    AsPlainString(rawReason) == "queue_pressure")
                {
                    preservedPayload["reason"] = "queue_pressure";
                    rawPayload.Remove("reason");
                }
            }

            Dictionary<string, object?>? rawContext = null;
            var preservedContext = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (eventEnvelope.Context != null)
            {
                rawContext = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var item in eventEnvelope.Context)
                {
                    rawContext[item.Key] = item.Value;
                }
                if (!TryPreserveBlock(rawContext, "evidence_reference", preservedContext, SanitizeEvidenceReferenceBlock))
                {
                    return null;
                }
                if (!TryPreserveBlock(rawContext, "evidence_limits", preservedContext, SanitizeEvidenceLimitsBlock))
                {
                    return null;
                }
            }

            var fields = new Dictionary<string, object?>
            {
                ["service"] = new Dictionary<string, object?>
                {
                    ["name"] = eventEnvelope.Service.Name,
                    ["runtime"] = eventEnvelope.Service.Runtime,
                    ["framework"] = eventEnvelope.Service.Framework,
                    ["environment"] = eventEnvelope.Service.Environment
                },
                ["payload"] = diagnosticPayload == null ? rawPayload : new Dictionary<string, object?>(),
                ["context"] = rawContext
            };
            if (Protect(fields, additionalFields, exactFields) is not Dictionary<string, object?> safe ||
                safe["service"] is not Dictionary<string, object?> service ||
                service["name"] is not string name ||
                service["environment"] is not string environment ||
                service["runtime"] is not string runtime)
            {
                return null;
            }

            Dictionary<string, object?> protectedPayload;
            if (diagnosticPayload != null)
            {
                protectedPayload = diagnosticPayload;
            }
            else
            {
                if (safe["payload"] is not Dictionary<string, object?> sanitizedPayload)
                {
                    return null;
                }
                foreach (var item in preservedPayload)
                {
                    sanitizedPayload[item.Key] = item.Value;
                }
                protectedPayload = sanitizedPayload;
            }

            Dictionary<string, object?>? protectedContext = null;
            if (eventEnvelope.Context != null)
            {
                if (safe["context"] is not Dictionary<string, object?> sanitizedContext)
                {
                    return null;
                }
                foreach (var item in preservedContext)
                {
                    sanitizedContext[item.Key] = item.Value;
                }
                protectedContext = sanitizedContext;
            }

            var assembled = new Dictionary<string, object?>
            {
                ["service"] = service,
                ["payload"] = protectedPayload,
                ["context"] = protectedContext
            };
            if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(assembled)) > MaxBytes)
            {
                return null;
            }

            return new DebugBundleEventEnvelope
            {
                SchemaVersion = eventEnvelope.SchemaVersion,
                EventId = eventEnvelope.EventId,
                EventType = eventEnvelope.EventType,
                ProjectToken = eventEnvelope.ProjectToken,
                SdkName = eventEnvelope.SdkName,
                SdkVersion = eventEnvelope.SdkVersion,
                OccurredAt = eventEnvelope.OccurredAt,
                Correlation = eventEnvelope.Correlation,
                Service = new DebugBundleServiceDescriptor
                {
                    Name = name,
                    Runtime = runtime,
                    Framework = service["framework"] as string,
                    Environment = environment
                },
                Payload = protectedPayload,
                Context = protectedContext
            };
        }
        catch { return null; }
    }

    internal static Dictionary<string, object?>? TryPreserveEvidenceReference(object? value)
    {
        if (IsJsonNull(value)) return null;
        return TryNormalizeOwned(value, out var owned) == OwnedStatus.Ok && owned is Dictionary<string, object?> map
            ? SanitizeEvidenceReferenceBlock(map)
            : null;
    }

    internal static Dictionary<string, object?>? TryPreserveEvidenceLimits(object? value)
    {
        if (IsJsonNull(value)) return null;
        return TryNormalizeOwned(value, out var owned) == OwnedStatus.Ok && owned is Dictionary<string, object?> map
            ? SanitizeEvidenceLimitsBlock(map)
            : null;
    }

    private static bool TryPreserveBlock(
        Dictionary<string, object?> source,
        string key,
        Dictionary<string, object?> destination,
        Func<Dictionary<string, object?>, Dictionary<string, object?>?> sanitizer)
    {
        if (!source.TryGetValue(key, out var raw) || IsJsonNull(raw))
        {
            return true;
        }
        var status = TryNormalizeOwned(raw, out var owned);
        if (status == OwnedStatus.Unsafe)
        {
            return false;
        }
        if (owned is Dictionary<string, object?> map)
        {
            var safe = sanitizer(map);
            if (safe != null)
            {
                destination[key] = safe;
                source.Remove(key);
            }
        }
        return true;
    }

    private static bool IsJsonNull(object? value)
        => value == null || value is JsonElement element && element.ValueKind == JsonValueKind.Null;

    private static string? AsPlainString(object? value)
        => value is string text ? text : value is JsonElement element && element.ValueKind == JsonValueKind.String ? element.GetString() : null;

    private static OwnedStatus TryNormalizeOwned(object? value, out object? owned)
        => TryNormalizeOwned(value, new OwnedBudget(), 0, out owned);

    private static OwnedStatus TryNormalizeOwned(object? value, OwnedBudget budget, int depth, out object? owned)
    {
        owned = null;
        if (++budget.Nodes > 4096 || depth > 16) return OwnedStatus.Unsafe;
        if (value == null || value is bool)
        {
            owned = value;
            return OwnedStatus.Ok;
        }
        if (value is string text)
        {
            budget.Bytes += Encoding.UTF8.GetByteCount(text);
            if (budget.Bytes > MaxBytes) return OwnedStatus.Unsafe;
            owned = text;
            return OwnedStatus.Ok;
        }
        if (value is byte or sbyte or short or ushort or int or uint or long or ulong or decimal)
        {
            owned = value;
            return OwnedStatus.Ok;
        }
        if (value is double number)
        {
            if (double.IsNaN(number) || double.IsInfinity(number)) return OwnedStatus.Unsafe;
            owned = value;
            return OwnedStatus.Ok;
        }
        if (value is float fraction)
        {
            if (float.IsNaN(fraction) || float.IsInfinity(fraction)) return OwnedStatus.Unsafe;
            owned = value;
            return OwnedStatus.Ok;
        }
        if (value is JsonElement element)
        {
            return TryNormalizeJson(element, budget, depth, out owned);
        }
        if (!IsSafeContainer(value))
        {
            return OwnedStatus.Unsafe;
        }
        if (!budget.Seen.Add(value))
        {
            return OwnedStatus.Unsafe;
        }
        try
        {
            if (value is IDictionary dictionary)
            {
                if (dictionary.Count > 256) return OwnedStatus.Unsafe;
                var map = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Key is not string key) return OwnedStatus.Unsafe;
                    budget.Bytes += Encoding.UTF8.GetByteCount(key);
                    if (budget.Bytes > MaxBytes) return OwnedStatus.Unsafe;
                    if (TryNormalizeOwned(entry.Value, budget, depth + 1, out var item) != OwnedStatus.Ok) return OwnedStatus.Unsafe;
                    map[key] = item;
                }
                owned = map;
                return OwnedStatus.Ok;
            }
            if (value is IEnumerable enumerable)
            {
                var list = new List<object?>();
                foreach (var entry in enumerable)
                {
                    if (list.Count >= 256) return OwnedStatus.Unsafe;
                    if (TryNormalizeOwned(entry, budget, depth + 1, out var item) != OwnedStatus.Ok) return OwnedStatus.Unsafe;
                    list.Add(item);
                }
                owned = list;
                return OwnedStatus.Ok;
            }
            return OwnedStatus.Unsafe;
        }
        finally
        {
            budget.Seen.Remove(value);
        }
    }

    private static OwnedStatus TryNormalizeJson(JsonElement element, OwnedBudget budget, int depth, out object? owned)
    {
        owned = null;
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var map = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (map.Count >= 256) return OwnedStatus.Unsafe;
                    budget.Bytes += Encoding.UTF8.GetByteCount(property.Name);
                    if (budget.Bytes > MaxBytes) return OwnedStatus.Unsafe;
                    if (TryNormalizeOwned(property.Value, budget, depth + 1, out var item) != OwnedStatus.Ok) return OwnedStatus.Unsafe;
                    map[property.Name] = item;
                }
                owned = map;
                return OwnedStatus.Ok;
            case JsonValueKind.Array:
                if (element.GetArrayLength() > 256) return OwnedStatus.Unsafe;
                var list = new List<object?>();
                foreach (var item in element.EnumerateArray())
                {
                    if (TryNormalizeOwned(item, budget, depth + 1, out var nested) != OwnedStatus.Ok) return OwnedStatus.Unsafe;
                    list.Add(nested);
                }
                owned = list;
                return OwnedStatus.Ok;
            case JsonValueKind.String:
                var text = element.GetString()!;
                budget.Bytes += Encoding.UTF8.GetByteCount(text);
                if (budget.Bytes > MaxBytes) return OwnedStatus.Unsafe;
                owned = text;
                return OwnedStatus.Ok;
            case JsonValueKind.Number:
                if (element.TryGetInt64(out var integer))
                {
                    owned = integer >= int.MinValue && integer <= int.MaxValue ? (object)(int)integer : integer;
                    return OwnedStatus.Ok;
                }
                var number = element.GetDouble();
                if (double.IsNaN(number) || double.IsInfinity(number)) return OwnedStatus.Unsafe;
                owned = number;
                return OwnedStatus.Ok;
            case JsonValueKind.True:
                owned = true;
                return OwnedStatus.Ok;
            case JsonValueKind.False:
                owned = false;
                return OwnedStatus.Ok;
            case JsonValueKind.Null:
                owned = null;
                return OwnedStatus.Ok;
            default:
                return OwnedStatus.Unsafe;
        }
    }

    private static string? ScrubProtocolString(string value, IEnumerable<string>? additionalFields, IEnumerable<string>? exactFields)
        => Protect(value, additionalFields, exactFields) as string;

    private static bool TryScrubNullableString(
        object? raw,
        IEnumerable<string>? additionalFields,
        IEnumerable<string>? exactFields,
        out string? scrubbed)
    {
        scrubbed = null;
        if (raw == null) return true;
        if (raw is not string text) return false;
        scrubbed = ScrubProtocolString(text, additionalFields, exactFields);
        return scrubbed != null;
    }

    private static string SanitizeBrowserStackUrls(string value)
    {
        return StackUrlPattern.Replace(value, match =>
        {
            var source = match.Value;
            var coordMatch = StackCoordinateSuffix.Match(source);
            var suffix = coordMatch.Success
                ? coordMatch.Value
                : StackPunctuationSuffix.Match(source) is { Success: true } punctMatch ? punctMatch.Value : string.Empty;
            var location = suffix.Length == 0 ? source : source.Substring(0, source.Length - suffix.Length);
            if (!Uri.TryCreate(location, UriKind.Absolute, out var uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https") ||
                string.IsNullOrEmpty(uri.Host))
            {
                return "[unavailable-url]" + suffix;
            }
            var path = uri.AbsolutePath.Length == 0 ? "/" : uri.AbsolutePath;
            return uri.Scheme + "://" + uri.Authority + path + suffix;
        });
    }

    private static bool HasOnlyAllowedKeys(Dictionary<string, object?> map, HashSet<string> allowed)
        => map.Keys.All(allowed.Contains);

    private static bool TryReadInteger(object? value, out long result)
    {
        switch (value)
        {
            case byte b: result = b; return true;
            case sbyte sb: result = sb; return true;
            case short s: result = s; return true;
            case ushort us: result = us; return true;
            case int i: result = i; return true;
            case uint ui: result = ui; return true;
            case long l: result = l; return true;
            case ulong ul when ul <= long.MaxValue: result = (long)ul; return true;
            case decimal d when decimal.Truncate(d) == d && d >= long.MinValue && d <= long.MaxValue:
                result = (long)d;
                return true;
            case double d when !double.IsNaN(d) && !double.IsInfinity(d) && Math.Truncate(d) == d && d >= -MaxSafeInteger && d <= MaxSafeInteger:
                result = (long)d;
                return true;
            case float f when !float.IsNaN(f) && !float.IsInfinity(f) && Math.Truncate(f) == f && f >= -16777216f && f <= 16777216f:
                result = (long)f;
                return true;
            default:
                result = 0;
                return false;
        }
    }

    private static object BoxInteger(long value)
        => value >= int.MinValue && value <= int.MaxValue ? (object)(int)value : value;

    private static bool TryReadNonNegativeNumber(object? value, out object? preserved)
    {
        preserved = null;
        switch (value)
        {
            case byte or ushort or uint or ulong:
                preserved = value;
                return true;
            case sbyte sb when sb >= 0: preserved = value; return true;
            case short s when s >= 0: preserved = value; return true;
            case int i when i >= 0: preserved = value; return true;
            case long l when l >= 0: preserved = value; return true;
            case decimal d when d >= 0: preserved = value; return true;
            case double d when !double.IsNaN(d) && !double.IsInfinity(d) && d >= 0: preserved = value; return true;
            case float f when !float.IsNaN(f) && !float.IsInfinity(f) && f >= 0: preserved = value; return true;
            default: return false;
        }
    }

    private static bool IsVersionOne(Dictionary<string, object?> map)
        => map.TryGetValue("version", out var version) && TryReadInteger(version, out var number) && number == 1;

    private static bool FitsUtf8(string value, int maxBytes)
        => Encoding.UTF8.GetByteCount(value) <= maxBytes;

}
