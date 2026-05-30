using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DebugBundle;

internal static class RemoteProbeTokenValidator
{
    private const string Prefix = "dbundle_probe_";

    public static IReadOnlyList<ProbeDirective> Validate(string? token, string? signingKey, string service, string environment, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(signingKey) || !token!.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return Array.Empty<ProbeDirective>();
        }

        var body = token.Substring(Prefix.Length);
        var parts = body.Split('.');
        if (parts.Length != 2)
        {
            return Array.Empty<ProbeDirective>();
        }

        byte[] payloadBytes;
        byte[] signatureBytes;
        try
        {
            payloadBytes = Base64UrlDecode(parts[0]);
            signatureBytes = Base64UrlDecode(parts[1]);
        }
        catch
        {
            return Array.Empty<ProbeDirective>();
        }

        byte[] expected;
        using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(signingKey)))
        {
            expected = hmac.ComputeHash(Encoding.ASCII.GetBytes(parts[0]));
        }

        if (!ConstantTimeEquals(expected, signatureBytes))
        {
            return Array.Empty<ProbeDirective>();
        }

        TriggerTokenPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<TriggerTokenPayload>(payloadBytes, Transport.JsonSerialization.Options);
        }
        catch
        {
            payload = null;
        }

        if (payload == null || payload.TriggerExpiresAt <= now)
        {
            return Array.Empty<ProbeDirective>();
        }

        if (!ScopeMatches(payload.Service, service) || !ScopeMatches(payload.Environment, environment))
        {
            return Array.Empty<ProbeDirective>();
        }

        var patterns = payload.LabelPatterns.Count > 0 ? payload.LabelPatterns : payload.Labels;
        return patterns
            .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
            .Select(pattern => new ProbeDirective
            {
                ActivationId = payload.ActivationId,
                LabelPattern = pattern,
                Service = payload.Service,
                Environment = payload.Environment,
                ExpiresAt = payload.TriggerExpiresAt
            })
            .ToArray();
    }

    private static bool ScopeMatches(string? configured, string actual)
    {
        return string.IsNullOrWhiteSpace(configured) || configured == "*" || configured!.Equals(actual, StringComparison.Ordinal);
    }

    private static bool ConstantTimeEquals(byte[] expected, byte[] actual)
    {
        if (expected.Length != actual.Length)
        {
            return false;
        }

        var difference = 0;
        for (var index = 0; index < expected.Length; index++)
        {
            difference |= expected[index] ^ actual[index];
        }

        return difference == 0;
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2:
                padded += "==";
                break;
            case 3:
                padded += "=";
                break;
        }

        return Convert.FromBase64String(padded);
    }

    private sealed class TriggerTokenPayload
    {
        [JsonPropertyName("activation_id")]
        public string? ActivationId { get; set; }

        [JsonPropertyName("label_patterns")]
        public List<string> LabelPatterns { get; set; } = new();

        [JsonPropertyName("labels")]
        public List<string> Labels { get; set; } = new();

        [JsonPropertyName("service")]
        public string? Service { get; set; }

        [JsonPropertyName("environment")]
        public string? Environment { get; set; }

        [JsonPropertyName("trigger_expires_at")]
        public DateTimeOffset TriggerExpiresAt { get; set; }
    }
}
