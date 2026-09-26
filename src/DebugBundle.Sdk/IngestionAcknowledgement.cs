using System.Text.Json;

namespace DebugBundle;

internal enum IngestionAcknowledgementKind
{
    Legacy,
    ProtocolFailure,
    Acknowledged
}

internal sealed class IngestionAcknowledgementDecision
{
    public IngestionAcknowledgementKind Kind { get; set; }
    public int Accepted { get; set; }
    public IReadOnlyCollection<int> RetryableIndices { get; set; } = Array.Empty<int>();
}

internal static class IngestionAcknowledgement
{
    private static readonly HashSet<string> RetryableReasons = new(StringComparer.Ordinal)
    {
        "rate_limited",
        "monthly_quota_exceeded",
        "analytics_quota_exceeded"
    };

    public static IngestionAcknowledgementDecision Decide(string? body, int batchLength, bool required = false)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return required ? ProtocolFailure() : new IngestionAcknowledgementDecision { Kind = IngestionAcknowledgementKind.Legacy };
        }

        try
        {
            using var document = JsonDocument.Parse(body!);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return ProtocolFailure();
            }

            var root = document.RootElement;
            var hasAccepted = root.TryGetProperty("accepted", out var acceptedElement);
            var hasRejected = root.TryGetProperty("rejected", out var rejectedElement);
            var hasErrors = root.TryGetProperty("errors", out var errorsElement);
            if (!hasAccepted && !hasRejected && !hasErrors)
            {
                return required ? ProtocolFailure() : new IngestionAcknowledgementDecision { Kind = IngestionAcknowledgementKind.Legacy };
            }

            if (!hasAccepted || !hasRejected || !hasErrors ||
                acceptedElement.ValueKind != JsonValueKind.Number || rejectedElement.ValueKind != JsonValueKind.Number ||
                !acceptedElement.TryGetInt32(out var accepted) || accepted < 0 ||
                !rejectedElement.TryGetInt32(out var rejected) || rejected < 0 ||
                errorsElement.ValueKind != JsonValueKind.Array ||
                accepted + rejected != batchLength ||
                errorsElement.GetArrayLength() != rejected)
            {
                return ProtocolFailure();
            }

            var seen = new HashSet<int>();
            var retryableIndices = new HashSet<int>();
            foreach (var error in errorsElement.EnumerateArray())
            {
                if (error.ValueKind != JsonValueKind.Object ||
                    !error.TryGetProperty("index", out var indexElement) ||
                    indexElement.ValueKind != JsonValueKind.Number ||
                    !indexElement.TryGetInt32(out var index) ||
                    index < 0 || index >= batchLength || !seen.Add(index) ||
                    !error.TryGetProperty("reason", out var reasonElement) ||
                    reasonElement.ValueKind != JsonValueKind.String ||
                    string.IsNullOrEmpty(reasonElement.GetString()))
                {
                    return ProtocolFailure();
                }

                if (RetryableReasons.Contains(reasonElement.GetString()!))
                {
                    retryableIndices.Add(index);
                }
            }

            return new IngestionAcknowledgementDecision
            {
                Kind = IngestionAcknowledgementKind.Acknowledged,
                Accepted = accepted,
                RetryableIndices = retryableIndices
            };
        }
        catch (JsonException)
        {
            return ProtocolFailure();
        }
    }

    private static IngestionAcknowledgementDecision ProtocolFailure() =>
        new() { Kind = IngestionAcknowledgementKind.ProtocolFailure };
}
