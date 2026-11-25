using System.Collections;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DebugBundle.Redaction;

/// <summary>Mandatory bounded protection for application-owned telemetry fields.</summary>
public static partial class TelemetryPrivacy
{
    private const string Redacted = "[REDACTED]";
    private const int MaxBytes = 256 * 1024;
    private const int MaxCustomKeys = 128;
    private const int MaxCustomKeyLength = 64;
    private static readonly HashSet<string> EmptyExactKeys = new(StringComparer.Ordinal);
    private static readonly string[] Fields =
    {
        "password", "secret", "token", "api_key", "apikey", "access_token", "refresh_token",
        "private_key", "accessToken", "refreshToken", "privateKey", "clientSecret", "passwd", "card_number", "credit_card", "cvv", "cvc", "pin", "expiry",
        "phone", "bearer", "session_id", "otp", "verification_code", "authorization", "cookie",
        "ssn", "client_secret", "x_api_key", "set_cookie", "proxy_authorization"
    };
    private const RegexOptions IgnoreCase = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);
    // The fixed field set exceeds the runtime's small static Regex cache. Retain
    // its immutable patterns once; caller-specific rules remain operation-owned.
    private static readonly Regex[] FieldPatterns = Fields.Select(FieldPattern).ToArray();
    private static Regex FieldPattern(string field) => new("\\b(" + Regex.Escape(field) + ")\\b([\"']?\\s*[:=]\\s*)(?:\"[^\"]*\"|'[^']*'|[^\\s&,;]+)", IgnoreCase, MatchTimeout);
    // Identifier boundaries redact `tag=secret` while preserving `tag_name=script` and `x-tag-name=script`.
    private static Regex ExactFieldPattern(string field) => new("(?<![A-Za-z0-9_-])(" + Regex.Escape(field.Trim()) + ")(?![A-Za-z0-9_-])([\"']?\\s*[:=]\\s*)(?:\"[^\"]*\"|'[^']*'|[^\\s&,;]+)", IgnoreCase, MatchTimeout);

    public static object? Protect(object? value, IEnumerable<string>? additionalFields = null)
        => Protect(value, additionalFields, exactFields: null);

    public static object? Protect(object? value, IEnumerable<string>? additionalFields, IEnumerable<string>? exactFields)
    {
        var extra = additionalFields?.Take(MaxCustomKeys + 1).ToArray() ?? Array.Empty<string>();
        var exact = exactFields?.Take(MaxCustomKeys + 1).ToArray() ?? Array.Empty<string>();
        if (extra.Length + exact.Length > MaxCustomKeys ||
            extra.Any(field => string.IsNullOrWhiteSpace(field) || field.Length > MaxCustomKeyLength) ||
            exact.Any(field => string.IsNullOrWhiteSpace(field) || field.Length > MaxCustomKeyLength))
        {
            throw new ArgumentException("unsafe_input");
        }

        var work = new Work(extra, exact);
        var result = Visit(value, work, 0, true);
        if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(result)) > MaxBytes)
        {
            throw new ArgumentException("budget_exceeded");
        }
        return result;
    }

    private sealed class Work
    {
        public int Nodes;
        public int Bytes;
        public readonly HashSet<object> Seen = new(ReferenceComparer.Instance);
        public readonly HashSet<string> Keys;
        public readonly Regex[] ExtraPatterns;
        public readonly HashSet<string> ExactKeys;
        public readonly Regex[] ExactPatterns;

        public Work(string[] extra, string[] exact)
        {
            ExtraPatterns = extra.Length == 0 ? Array.Empty<Regex>() : extra.Select(FieldPattern).ToArray();
            Keys = new HashSet<string>(Fields.Concat(extra).Select(Canonical), StringComparer.Ordinal);
            ExactKeys = exact.Length == 0
                ? EmptyExactKeys
                : new HashSet<string>(exact.Select(field => AsciiLower(field.Trim())), StringComparer.Ordinal);
            ExactPatterns = exact.Length == 0 ? Array.Empty<Regex>() : exact.Select(ExactFieldPattern).ToArray();
        }
    }

    internal static string AsciiLower(string value)
    {
        var changed = false;
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] is >= 'A' and <= 'Z')
            {
                changed = true;
                break;
            }
        }
        if (!changed) return value;
        var chars = value.ToCharArray();
        for (var index = 0; index < chars.Length; index++)
        {
            if (chars[index] is >= 'A' and <= 'Z')
            {
                chars[index] = (char)(chars[index] + ('a' - 'A'));
            }
        }
        return new string(chars);
    }

    private static bool ExactSensitiveKey(string key, Work work)
        => work.ExactKeys.Count > 0 && work.ExactKeys.Contains(AsciiLower(key.Trim()));

    private sealed class ReferenceComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceComparer Instance = new();
        public new bool Equals(object? left, object? right) => ReferenceEquals(left, right);
        public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
    }

    private static string Canonical(string value) => Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]", string.Empty);

    private static bool SensitiveKey(string key, Work work)
    {
        var segments = Regex.Split(Regex.Replace(key, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant(), "[^a-z0-9]+");
        for (var start = 0; start < segments.Length; start++)
        {
            var combined = new StringBuilder();
            for (var end = start; end < segments.Length; end++)
            {
                combined.Append(Canonical(segments[end]));
                if (work.Keys.Contains(combined.ToString())) return true;
            }
        }
        return false;
    }

    private static void Count(Work work, string text)
    {
        work.Bytes += Encoding.UTF8.GetByteCount(text);
        if (work.Bytes > MaxBytes) throw new ArgumentException("budget_exceeded");
    }

    // Exact framework containers cannot override Count, enumeration, or index access.
    // Wrappers around arbitrary application collections are intentionally unsupported.
    internal static bool IsSafeContainer(object value)
    {
        var type = value.GetType();
        if (type.IsArray) return type.GetArrayRank() == 1;
        if (type == typeof(Hashtable) || type == typeof(ArrayList)) return true;
        if (!type.IsGenericType) return false;
        var definition = type.GetGenericTypeDefinition();
        return definition == typeof(List<>) ||
            (definition == typeof(Dictionary<,>) && type.GetGenericArguments()[0] == typeof(string));
    }

    private static object? Visit(object? value, Work work, int depth, bool structured)
    {
        if (++work.Nodes > 4096) throw new ArgumentException("budget_exceeded");
        if (depth > 16) return Redacted;
        if (value is JsonElement element) return VisitJson(element, work, depth, structured);
        if (value is string text) return CleanString(text, work, structured);
        if (value is IDictionary dictionary && IsSafeContainer(value))
        {
            if (!work.Seen.Add(dictionary)) return "[Circular]";
            try
            {
                if (dictionary.Count > 256) return Redacted;
                var output = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Key is not string key) throw new ArgumentException("unsafe_input");
                    if (key.Length > 128) continue;
                    Count(work, key);
                    if (ScrubText(key, work) != key) continue;
                    output[key] = SensitiveKey(key, work) || ExactSensitiveKey(key, work) ? Redacted : Visit(entry.Value, work, depth + 1, structured);
                }
                return output;
            }
            finally { work.Seen.Remove(dictionary); }
        }
        if (value is IEnumerable enumerable && IsSafeContainer(value))
        {
            if (!work.Seen.Add(enumerable)) return "[Circular]";
            try
            {
                var output = new List<object?>();
                foreach (var item in enumerable)
                {
                    if (output.Count >= 256) return Redacted;
                    output.Add(Visit(item, work, depth + 1, structured));
                }
                return output;
            }
            finally { work.Seen.Remove(enumerable); }
        }
        if (value is null or bool or byte or sbyte or short or ushort or int or uint or long or ulong or decimal) return value;
        if (value is double number && !double.IsNaN(number) && !double.IsInfinity(number)) return value;
        if (value is float fraction && !float.IsNaN(fraction) && !float.IsInfinity(fraction)) return value;
        if (value is char or DateTime or DateTimeOffset or Guid or TimeSpan or Enum) return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
        return "[Unsupported value]";
    }

    private static object? VisitJson(JsonElement element, Work work, int depth, bool structured)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var map = new Dictionary<string, object?>();
                foreach (var property in element.EnumerateObject())
                {
                    if (map.Count >= 256) return Redacted;
                    map[property.Name] = property.Value;
                }
                return Visit(map, work, depth, structured);
            case JsonValueKind.Array:
                if (element.GetArrayLength() > 256) return Redacted;
                return Visit(element.EnumerateArray().ToArray(), work, depth, structured);
            case JsonValueKind.String: return Visit(element.GetString(), work, depth, structured);
            case JsonValueKind.Number:
                if (element.TryGetInt64(out var integer)) return integer >= int.MinValue && integer <= int.MaxValue ? (object)(int)integer : integer;
                return element.GetDouble();
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.Null: return null;
            default: throw new ArgumentException("unsafe_input");
        }
    }

    private static object CleanString(string text, Work work, bool structured)
    {
        if (text.Length > 16 * 1024 || Encoding.UTF8.GetByteCount(text) > 16 * 1024) return Redacted;
        Count(work, text);
        if (structured && (text.StartsWith("{", StringComparison.Ordinal) || text.StartsWith("[", StringComparison.Ordinal)))
        {
            try
            {
                using var parsed = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 17 });
                if (parsed.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    return JsonSerializer.Serialize(VisitJson(parsed.RootElement, work, 0, false));
                }
            }
            catch (JsonException) { }
        }
        var cleaned = ScrubText(text, work);
        if (cleaned == text && (text.StartsWith("{", StringComparison.Ordinal) || text.StartsWith("[", StringComparison.Ordinal)) &&
            Regex.IsMatch(text, "(?:password|token|secret|authorization|cookie)[\"']?\\s*[:=]", IgnoreCase, MatchTimeout))
        {
            return Redacted;
        }
        return cleaned;
    }

    private static string ScrubText(string text, Work work, bool scanUrls = true)
    {
        const string pem = "-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----[\\s\\S]*?-----END (?:RSA |EC |OPENSSH )?PRIVATE KEY-----";
        if (Regex.IsMatch(text, "-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----", IgnoreCase, MatchTimeout) &&
            !Regex.IsMatch(text, pem, IgnoreCase, MatchTimeout)) return Redacted;
        var output = Regex.IsMatch(text, "(?:password|token|secret|authorization|cookie)%3[ad]", IgnoreCase, MatchTimeout)
            ? Uri.UnescapeDataString(text) : text;
        output = Regex.Replace(output, pem, Redacted, IgnoreCase, MatchTimeout);
        output = Regex.Replace(output, "\\b(Authorization|Proxy-Authorization|Cookie|Set-Cookie)\\s*:\\s*[^\\r\\n]*", "$1: [REDACTED]", IgnoreCase, MatchTimeout);
        output = Regex.Replace(output, "\\b(Bearer|Basic)\\s+[A-Za-z0-9._~+/-]{6,}", "$1 [REDACTED]", IgnoreCase, MatchTimeout);
        output = Regex.Replace(output, "\\bdbundle_(?:proj|mem|probe|agent|invite|org_invite|org_transfer)_[A-Za-z0-9_-]+\\b", Redacted, RegexOptions.None, MatchTimeout);
        foreach (var pattern in FieldPatterns)
        {
            output = pattern.Replace(output, "$1$2[REDACTED]");
        }
        foreach (var pattern in work.ExtraPatterns)
        {
            output = pattern.Replace(output, "$1$2[REDACTED]");
        }
        foreach (var pattern in work.ExactPatterns)
        {
            output = pattern.Replace(output, "$1$2[REDACTED]");
        }
        output = Regex.Replace(output, "(?<![A-Za-z0-9_-])(?:[0-9][ -]?){12,18}[0-9](?![A-Za-z0-9_-])",
            match => ValidCard(match.Value) ? Redacted : match.Value, RegexOptions.None, MatchTimeout);
        if (!scanUrls) return output;
        return Regex.Replace(output, "\\bhttps?://[^\\s<>\"']+", match =>
        {
            var raw = match.Value.TrimEnd(')', '.', ',', ';');
            return ScrubUrl(raw, work) + match.Value.Substring(raw.Length);
        }, IgnoreCase, MatchTimeout);
    }

    private static bool ValidCard(string value)
    {
        var digits = new string(value.Where(char.IsDigit).ToArray());
        if (digits.Length < 13 || digits.Length > 19 || digits.All(ch => ch == digits[0])) return false;
        var sum = 0;
        for (var index = digits.Length - 1; index >= 0; index--)
        {
            var digit = digits[index] - '0';
            if ((digits.Length - index) % 2 == 0) { digit *= 2; if (digit > 9) digit -= 9; }
            sum += digit;
        }
        return sum % 10 == 0;
    }

    private static string ScrubUrl(string raw, Work work)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Host.Length == 0)
            return Redacted;
        var output = new StringBuilder(uri.Scheme).Append("://");
        if (uri.UserInfo.Length > 0) output.Append("REDACTED@");
        output.Append(uri.Host);
        if (!uri.IsDefaultPort) output.Append(':').Append(uri.Port);
        output.Append(uri.AbsolutePath.Length == 0 ? "/" : uri.AbsolutePath);
        if (uri.Query.Length > 1)
        {
            var pairs = uri.Query.Substring(1).Split('&').Select(pair =>
            {
                var parts = pair.Split(new[] { '=' }, 2);
                var key = Uri.UnescapeDataString(parts[0]);
                var value = parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
                if (key.Length > 128 || ScrubText(key, work, false) != key) return null;
                return Uri.EscapeDataString(key) + "=" + Uri.EscapeDataString(SensitiveKey(key, work) || ExactSensitiveKey(key, work) || ScrubText(value, work, false) != value ? Redacted : value);
            }).Where(pair => pair is not null);
            output.Append('?').Append(string.Join("&", pairs));
        }
        return output.ToString();
    }
}
