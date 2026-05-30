using System.Collections.Concurrent;
using System.Text.Json;
using DebugBundle.Transport;
using Microsoft.AspNetCore.Http;

namespace DebugBundle.AspNetCore.Relay;

public static class DebugBundleRelayHandler
{
    private const string BrowserSdkName = "@debugbundle/sdk-browser";
    private const string DeliveredMarkerSuffix = ".delivered";
    private static readonly HashSet<string> AcceptedEventTypes = new(StringComparer.Ordinal)
    {
        "frontend_exception",
        "error_suppressed",
        "frontend_breadcrumb",
        "request_event",
        "probe_event"
    };

    private static readonly RelayRateLimiter RateLimiter = new();

    public static async Task HandleAsync(
        HttpContext context,
        DebugBundleOptions sdkOptions,
        DebugBundleRelayOptions relayOptions,
        IDebugBundleClient? client = null,
        CancellationToken cancellationToken = default)
    {
        var origin = RequestOrigin(context.Request);
        if (!OriginAllowed(context.Request, relayOptions, origin))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        SetCorsHeaders(context.Response, origin!);
        if (HttpMethods.IsOptions(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        if (!HttpMethods.IsPost(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        if (!HasJsonContentType(context.Request.ContentType))
        {
            await WriteJsonAsync(context, StatusCodes.Status400BadRequest, new RelayResponse(0, 0, new[] { "Relay requests must use Content-Type: application/json." }), cancellationToken).ConfigureAwait(false);
            return;
        }

        if (context.Request.ContentLength > relayOptions.MaxBodyBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        var clientIp = ClientIp(context, relayOptions.TrustForwardedHeaders);
        if (!RateLimiter.Allow(clientIp + "|" + relayOptions.GetHashCode(), relayOptions.RateLimitPerMinute))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }

        byte[] body;
        try
        {
            body = await ReadLimitedBodyAsync(context.Request, relayOptions.MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (PayloadTooLargeException)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        RelayPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<RelayPayload>(body, JsonSerialization.Options);
        }
        catch (JsonException)
        {
            payload = null;
        }

        if (payload?.Batch == null)
        {
            await WriteJsonAsync(context, StatusCodes.Status400BadRequest, new RelayResponse(0, 0, new[] { "Relay request body must include a batch array." }), cancellationToken).ConfigureAwait(false);
            return;
        }

        var accepted = new List<DebugBundleEventEnvelope>();
        var errors = new List<string>();
        for (var index = 0; index < payload.Batch.Count; index++)
        {
            try
            {
                accepted.Add(SanitizeEvent(payload.Batch[index], sdkOptions, relayOptions));
            }
            catch (InvalidRelayEventException exception)
            {
                errors.Add($"batch[{index}]: {exception.Message}");
            }
        }

        if (accepted.Count > 0)
        {
            var delivered = await DeliverAcceptedAsync(accepted, sdkOptions, relayOptions, cancellationToken).ConfigureAwait(false);
            if (!delivered)
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                return;
            }
        }

        await WriteJsonAsync(
            context,
            errors.Count == 0 ? StatusCodes.Status202Accepted : StatusCodes.Status400BadRequest,
            new RelayResponse(accepted.Count, errors.Count, errors),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> DeliverAcceptedAsync(
        IReadOnlyList<DebugBundleEventEnvelope> accepted,
        DebugBundleOptions sdkOptions,
        DebugBundleRelayOptions relayOptions,
        CancellationToken cancellationToken)
    {
        var projectMode = sdkOptions.ProjectMode;
        var token = ProjectToken(sdkOptions);

        if (projectMode == DebugBundleProjectMode.LocalOnly)
        {
            return (await SendWithFileTransportAsync(sdkOptions.LocalEventsDir, accepted, token, cancellationToken).ConfigureAwait(false)).Delivered;
        }

        if (relayOptions.DurableWrite)
        {
            var spoolResult = await SendWithFileTransportAsync(sdkOptions.SpoolDir, accepted, string.Empty, cancellationToken).ConfigureAwait(false);
            if (!spoolResult.Delivered)
            {
                return false;
            }

            var delivered = await SendWithHttpTransportAsync(sdkOptions, accepted, token, cancellationToken).ConfigureAwait(false);
            if (delivered)
            {
                MarkDelivered(spoolResult.WrittenFilePath);
            }

            return true;
        }

        return await SendWithHttpTransportAsync(sdkOptions, accepted, token, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<FileDeliveryResult> SendWithFileTransportAsync(string root, IReadOnlyList<DebugBundleEventEnvelope> accepted, string token, CancellationToken cancellationToken)
    {
        try
        {
            var transport = new FileEventTransport(root);
            var result = await transport.SendAsync(new EventTransportRequest { ProjectToken = token, Events = accepted }, cancellationToken).ConfigureAwait(false);
            return new FileDeliveryResult(result.StatusCode is >= 200 and < 300, result.WrittenFilePath);
        }
        catch
        {
            return new FileDeliveryResult(false, null);
        }
    }

    private static async Task<bool> SendWithHttpTransportAsync(DebugBundleOptions sdkOptions, IReadOnlyList<DebugBundleEventEnvelope> accepted, string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        try
        {
            using var transport = new HttpEventTransport(sdkOptions.Endpoint, sdkOptions.RequestTimeout);
            var result = await transport.SendAsync(new EventTransportRequest { ProjectToken = token, Events = accepted }, cancellationToken).ConfigureAwait(false);
            return result.StatusCode is >= 200 and < 300;
        }
        catch
        {
            return false;
        }
    }

    private static DebugBundleEventEnvelope SanitizeEvent(JsonElement candidate, DebugBundleOptions sdkOptions, DebugBundleRelayOptions relayOptions)
    {
        if (candidate.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidRelayEventException("Invalid browser relay event payload.");
        }

        var schemaVersion = RequiredString(candidate, "schema_version");
        var eventId = RequiredString(candidate, "event_id");
        var eventType = RequiredString(candidate, "event_type");
        if (!AcceptedEventTypes.Contains(eventType))
        {
            throw new InvalidRelayEventException($"Unsupported browser relay event type {eventType}.");
        }

        var occurredAt = RequiredString(candidate, "occurred_at");
        var sdkVersion = RequiredString(candidate, "sdk_version");
        var service = SanitizeService(candidate, sdkOptions, relayOptions);
        var payload = RequiredObject(candidate, "payload");
        StripSensitivePayloadFields(payload);

        var envelope = new DebugBundleEventEnvelope
        {
            SchemaVersion = schemaVersion,
            EventId = eventId,
            EventType = eventType,
            SdkName = BrowserSdkName,
            SdkVersion = sdkVersion,
            OccurredAt = occurredAt,
            Service = service,
            Payload = payload
        };

        if (candidate.TryGetProperty("correlation", out var correlationElement) && correlationElement.ValueKind == JsonValueKind.Object)
        {
            var correlation = KeepCorrelationFields(correlationElement);
            envelope.Correlation = correlation.Count == 0 ? null : correlation;
        }

        return envelope;
    }

    private static DebugBundleServiceDescriptor SanitizeService(JsonElement candidate, DebugBundleOptions sdkOptions, DebugBundleRelayOptions relayOptions)
    {
        if (!candidate.TryGetProperty("service", out var serviceElement) || serviceElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidRelayEventException("Invalid browser relay event payload.");
        }

        var name = FirstNonEmpty(relayOptions.Service, sdkOptions.Service, OptionalString(serviceElement, "name"));
        var environment = FirstNonEmpty(relayOptions.Environment, sdkOptions.Environment, OptionalString(serviceElement, "environment"));
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(environment))
        {
            throw new InvalidRelayEventException("Invalid browser relay event payload.");
        }

        return new DebugBundleServiceDescriptor
        {
            Name = name!,
            Runtime = OptionalString(serviceElement, "runtime") ?? "browser",
            Framework = OptionalString(serviceElement, "framework"),
            Environment = environment!
        };
    }

    private static Dictionary<string, object?> RequiredObject(JsonElement candidate, string propertyName)
    {
        if (!candidate.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidRelayEventException("Invalid browser relay event payload.");
        }

        return JsonSerializer.Deserialize<Dictionary<string, object?>>(element.GetRawText(), JsonSerialization.Options) ?? new Dictionary<string, object?>();
    }

    private static string RequiredString(JsonElement candidate, string propertyName)
    {
        if (!candidate.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.String)
        {
            throw new InvalidRelayEventException("Invalid browser relay event payload.");
        }

        var value = element.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidRelayEventException("Invalid browser relay event payload.");
        }

        return value;
    }

    private static string? OptionalString(JsonElement candidate, string propertyName)
    {
        return candidate.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
    }

    private static Dictionary<string, object?> KeepCorrelationFields(JsonElement correlationElement)
    {
        var kept = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var key in new[] { "trace_id", "request_id", "session_id", "user_id_hash" })
        {
            if (correlationElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
            {
                kept[key] = value.GetString();
            }
        }

        return kept;
    }

    private static void StripSensitivePayloadFields(IDictionary<string, object?> payload)
    {
        foreach (var key in new[] { "authorization", "cookie", "project_token", "organization_id" })
        {
            payload.Remove(key);
        }

        foreach (var containerKey in new[] { "headers", "request" })
        {
            if (payload.TryGetValue(containerKey, out var value) && value is JsonElement element)
            {
                payload[containerKey] = StripSensitiveJsonElement(element);
            }
        }
    }

    private static object? StripSensitiveJsonElement(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return JsonSerializer.Deserialize<object?>(element.GetRawText(), JsonSerialization.Options);
        }

        var result = JsonSerializer.Deserialize<Dictionary<string, object?>>(element.GetRawText(), JsonSerialization.Options) ?? new Dictionary<string, object?>();
        foreach (var key in result.Keys.Where(key => key.Equals("authorization", StringComparison.OrdinalIgnoreCase) || key.Equals("cookie", StringComparison.OrdinalIgnoreCase) || key.Equals("x-api-key", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            result.Remove(key);
        }

        if (result.TryGetValue("headers", out var nestedHeaders) && nestedHeaders is JsonElement nestedElement)
        {
            result["headers"] = StripSensitiveJsonElement(nestedElement);
        }

        return result;
    }

    private static bool OriginAllowed(HttpRequest request, DebugBundleRelayOptions options, string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin))
        {
            return false;
        }

        if (options.AllowedOrigins.Count > 0)
        {
            return options.AllowedOrigins.Any(candidate => NormalizeOrigin(candidate) == NormalizeOrigin(origin));
        }

        return NormalizeOrigin(InferredOrigin(request, options.TrustForwardedHeaders)) == NormalizeOrigin(origin);
    }

    private static string? RequestOrigin(HttpRequest request)
    {
        var origin = request.Headers["Origin"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(origin))
        {
            return origin;
        }

        var referer = request.Headers["Referer"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(referer) || !Uri.TryCreate(referer, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return $"{uri.Scheme}://{uri.Authority}";
    }

    private static string InferredOrigin(HttpRequest request, bool trustForwarded)
    {
        var scheme = request.Scheme;
        var host = request.Host.Value;
        if (trustForwarded)
        {
            scheme = FirstNonEmpty(request.Headers["X-Forwarded-Proto"].FirstOrDefault(), scheme)!;
            host = FirstNonEmpty(request.Headers["X-Forwarded-Host"].FirstOrDefault(), host)!;
        }

        return $"{scheme}://{host}";
    }

    private static string ClientIp(HttpContext context, bool trustForwarded)
    {
        if (trustForwarded)
        {
            var forwarded = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(forwarded))
            {
                return forwarded.Split(',')[0].Trim();
            }
        }

        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    private static bool HasJsonContentType(string? contentType)
    {
        return contentType != null && contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<byte[]> ReadLimitedBodyAsync(HttpRequest request, long maxBodyBytes, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await request.Body.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return memory.ToArray();
            }

            if (memory.Length + read > maxBodyBytes)
            {
                throw new PayloadTooLargeException();
            }

            memory.Write(buffer, 0, read);
        }
    }

    private static void SetCorsHeaders(HttpResponse response, string origin)
    {
        response.Headers["Access-Control-Allow-Origin"] = origin;
        response.Headers["Access-Control-Allow-Methods"] = "POST, OPTIONS";
        response.Headers["Access-Control-Allow-Headers"] = "content-type";
        response.Headers["Access-Control-Max-Age"] = "600";
        response.Headers["Vary"] = "Origin";
    }

    private static async Task WriteJsonAsync(HttpContext context, int status, RelayResponse response, CancellationToken cancellationToken)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body, response, JsonSerialization.Options, cancellationToken).ConfigureAwait(false);
    }

    private static string NormalizeOrigin(string value) => value.Trim().TrimEnd('/').ToLowerInvariant();

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static string ProjectToken(DebugBundleOptions sdkOptions)
    {
        return FirstNonEmpty(sdkOptions.ProjectToken, Environment.GetEnvironmentVariable("DEBUGBUNDLE_TOKEN")) ?? string.Empty;
    }

    private static void MarkDelivered(string? spoolPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(spoolPath))
            {
                return;
            }

            using var stream = new FileStream(spoolPath + DeliveredMarkerSuffix, FileMode.Create, FileAccess.Write, FileShare.None);
        }
        catch
        {
            // Marker failure must not turn a successfully spooled relay write into an application failure.
        }
    }

    private sealed class RelayPayload
    {
        public List<JsonElement>? Batch { get; set; }
    }

    private sealed class FileDeliveryResult
    {
        public FileDeliveryResult(bool delivered, string? writtenFilePath)
        {
            Delivered = delivered;
            WrittenFilePath = writtenFilePath;
        }

        public bool Delivered { get; }
        public string? WrittenFilePath { get; }
    }

    private sealed class RelayResponse
    {
        public RelayResponse(int accepted, int rejected, IReadOnlyCollection<string> errors)
        {
            Accepted = accepted;
            Rejected = rejected;
            Errors = errors;
        }

        public int Accepted { get; }
        public int Rejected { get; }
        public IReadOnlyCollection<string> Errors { get; }
    }

    private sealed class InvalidRelayEventException : Exception
    {
        public InvalidRelayEventException(string message)
            : base(message)
        {
        }
    }

    private sealed class PayloadTooLargeException : Exception
    {
    }

    private sealed class RelayRateLimiter
    {
        private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _entries = new();

        public bool Allow(string key, int limit)
        {
            var queue = _entries.GetOrAdd(key, _ => new Queue<DateTimeOffset>());
            lock (queue)
            {
                var now = DateTimeOffset.UtcNow;
                while (queue.Count > 0 && queue.Peek() < now.AddMinutes(-1))
                {
                    queue.Dequeue();
                }

                if (queue.Count >= Math.Max(1, limit))
                {
                    return false;
                }

                queue.Enqueue(now);
                return true;
            }
        }
    }
}
