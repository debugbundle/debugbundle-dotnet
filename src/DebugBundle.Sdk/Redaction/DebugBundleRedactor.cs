using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace DebugBundle.Redaction;

public sealed class DebugBundleRedactor
{
    public const string RedactedMarker = "[REDACTED]";
    private const int DefaultMaxDepth = 6;
    private const int DefaultMaxItems = 50;
    private const int DefaultMaxStringLength = 2048;

    private static readonly string[] DefaultSensitiveFields =
    {
        "password",
        "secret",
        "token",
        "api_key",
        "apikey",
        "access_token",
        "refresh_token",
        "private_key",
        "passwd",
        "card_number",
        "cvv",
        "cvc",
        "pin",
        "expiry",
        "phone",
        "bearer",
        "session_id",
        "otp",
        "verification_code",
        "authorization",
        "cookie",
        "ssn",
        "credit_card",
        "connection_string"
    };

    private readonly HashSet<string> _sensitive;

    public DebugBundleRedactor(IEnumerable<string>? additionalFields = null)
    {
        _sensitive = new HashSet<string>(DefaultSensitiveFields, StringComparer.OrdinalIgnoreCase);
        if (additionalFields == null)
        {
            return;
        }

        foreach (var field in additionalFields)
        {
            if (!string.IsNullOrWhiteSpace(field))
            {
                _sensitive.Add(field.Trim());
            }
        }
    }

    public object? Redact(object? value) => RedactValue(value, key: null, depth: 0, visited: new ReferenceSet());

    private object? RedactValue(object? value, string? key, int depth, ReferenceSet visited)
    {
        if (value == null)
        {
            return null;
        }

        if (IsSensitiveKey(key))
        {
            return RedactedMarker;
        }

        if (depth > DefaultMaxDepth)
        {
            return "[TruncatedDepth]";
        }

        if (value is string text)
        {
            return text.Length > DefaultMaxStringLength ? text.Substring(0, DefaultMaxStringLength) + "...[truncated]" : text;
        }

        if (value is bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal or DateTime or DateTimeOffset or Guid)
        {
            return value;
        }

        if (value is Enum)
        {
            return value.ToString();
        }

        if (!value.GetType().IsValueType && !visited.TryAdd(value))
        {
            return "[Circular]";
        }

        if (value is IDictionary dictionary)
        {
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            var count = 0;
            foreach (DictionaryEntry entry in dictionary)
            {
                if (count >= DefaultMaxItems)
                {
                    result["_truncated"] = "additional entries omitted";
                    break;
                }

                var entryKey = Convert.ToString(entry.Key, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                result[entryKey] = RedactValue(entry.Value, entryKey, depth + 1, visited);
                count++;
            }

            return result;
        }

        if (value is IEnumerable enumerable)
        {
            var result = new List<object?>();
            var count = 0;
            foreach (var item in enumerable)
            {
                if (count >= DefaultMaxItems)
                {
                    result.Add("...[truncated]");
                    break;
                }

                result.Add(RedactValue(item, key, depth + 1, visited));
                count++;
            }

            return result;
        }

        return RedactObject(value, depth, visited);
    }

    private Dictionary<string, object?> RedactObject(object value, int depth, ReferenceSet visited)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        var count = 0;
        foreach (var property in value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.GetIndexParameters().Length != 0 || !property.CanRead)
            {
                continue;
            }

            if (count >= DefaultMaxItems)
            {
                result["_truncated"] = "additional properties omitted";
                break;
            }

            object? propertyValue;
            try
            {
                propertyValue = property.GetValue(value);
            }
            catch
            {
                propertyValue = "[Unreadable]";
            }

            result[property.Name] = RedactValue(propertyValue, property.Name, depth + 1, visited);
            count++;
        }

        return result;
    }

    private bool IsSensitiveKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var compact = new string(key.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        if (_sensitive.Contains(compact))
        {
            return true;
        }

        return SplitSegments(key!).Any(segment => _sensitive.Contains(segment));
    }

    private static IEnumerable<string> SplitSegments(string key)
    {
        var normalized = Regex.Replace(key.Trim(), "([a-z0-9])([A-Z])", "$1_$2");
        var current = new StringBuilder();
        foreach (var ch in normalized)
        {
            if (char.IsLetterOrDigit(ch))
            {
                current.Append(char.ToLowerInvariant(ch));
                continue;
            }

            if (current.Length > 0)
            {
                yield return current.ToString();
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private sealed class ReferenceSet
    {
        private readonly HashSet<object> _values = new(ReferenceEqualityComparer.Instance);

        public bool TryAdd(object value) => _values.Add(value);
    }

    private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceEqualityComparer Instance = new();

        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
