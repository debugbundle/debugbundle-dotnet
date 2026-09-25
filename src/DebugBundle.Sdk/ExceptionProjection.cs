namespace DebugBundle;

// Caller snapshots use only the base Exception getters. Overrides and ToString run on
// the existing sender after admission; the queue owns only a weak reference to the graph.
internal static class ExceptionProjection
{
    private const string Unavailable = "Exception details unavailable";

    internal static Dictionary<string, object?> Snapshot(Exception exception, bool allowVirtual = false, bool includeInner = true)
    {
        var type = exception.GetType();
        var message = Message(exception, allowVirtual);
        var fallback = (type.FullName ?? type.Name) + ": " + message;
        string stack;
        try
        {
            if (allowVirtual) stack = exception.ToString();
            else stack = IsBaseGetter(exception, nameof(Exception.StackTrace)) && exception.StackTrace is { Length: > 0 } trace
                ? fallback + Environment.NewLine + trace : fallback;
        }
        catch { stack = fallback; }
        var result = new Dictionary<string, object?>
        {
            ["type"] = type.FullName ?? type.Name,
            ["message"] = message,
            ["stack"] = stack,
            ["hresult"] = exception.HResult
        };
        if (includeInner && exception.InnerException is { } inner)
        {
            var nested = Snapshot(inner, allowVirtual, includeInner: false);
            nested["name"] = nested["type"];
            nested.Remove("type");
            result["inner_exception"] = nested;
        }
        return result;
    }

    internal static string Message(Exception exception, bool allowVirtual = false)
    {
        try
        {
            if (!allowVirtual && !IsBaseGetter(exception, nameof(Exception.Message))) return Unavailable;
            var message = exception.Message;
            return string.IsNullOrWhiteSpace(message) ? exception.GetType().Name : message;
        }
        catch { return Unavailable; }
    }

    private static bool IsBaseGetter(Exception exception, string property) =>
        exception.GetType().GetProperty(property)?.GetMethod?.DeclaringType == typeof(Exception);

    internal static void Enrich(DebugBundleEventEnvelope envelope)
    {
        var pending = envelope.PendingException;
        envelope.PendingException = null;
        if (pending == null || !pending.TryGetTarget(out var exception)) return;
        var summary = Snapshot(exception, allowVirtual: true);
        if (envelope.EventType == "backend_exception")
        {
            envelope.Payload["message"] = summary["message"];
            envelope.Payload["stack"] = summary["stack"];
            envelope.Context ??= new Dictionary<string, object?>();
            envelope.Context["exception_hresult"] = summary["hresult"];
            if (summary.TryGetValue("inner_exception", out var inner)) envelope.Context["inner_exception"] = inner;
        }
        else if (envelope.EventType == "log_event" && envelope.Payload.TryGetValue("attributes", out var attributes) &&
                 attributes is Dictionary<string, object?> fields)
        {
            fields["exception"] = summary;
        }
    }
}
