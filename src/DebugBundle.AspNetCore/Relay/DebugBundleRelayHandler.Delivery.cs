using System.Text.Json;
using DebugBundle.Redaction;
using DebugBundle.Transport;

namespace DebugBundle.AspNetCore.Relay;

public static partial class DebugBundleRelayHandler
{
    // One held custom forwarder cannot create replacement workers on later requests.
    private static readonly SemaphoreSlim CustomForwardSlot = new(1, 1);
    private static bool HasTypedEvidence(DebugBundleEventEnvelope envelope)
    {
        if (envelope.EventType == "diagnostic_context" || new[] { "exception_evidence", "execution", "resource_observations" }.Any(envelope.Payload.ContainsKey)) return true;
        if (envelope.Context?.TryGetValue("evidence_reference", out var reference) != true) return false;
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(reference, OwnedJson.Options));
        return EvidenceWire.Reference(document.RootElement);
    }
    private static DebugBundleEventEnvelope? ForwardEvent(DebugBundleEventEnvelope envelope, DebugBundleOptions options, EvidenceDeliveryState state, bool native)
    {
        var copy = OwnedJson.Event(OwnedJson.Snapshot(envelope));
        if (copy.EventType == "diagnostic_context" && !state.Supports(copy.Payload["kind"] as string == "recovery" ? "recovery_v1" : "resource_observations_v1")) return null;
        if (!state.Supports("exception_evidence_v1")) { copy.Payload.Remove("exception_evidence"); copy.Payload.Remove("execution"); }
        if (!state.Supports("resource_observations_v1")) copy.Payload.Remove("resource_observations");
        if (!state.Supports("recovery_v1") && !state.Supports("resource_observations_v1") && copy.Context?.TryGetValue("evidence_reference", out var reference) == true)
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(reference, OwnedJson.Options));
            if (EvidenceWire.Reference(document.RootElement)) copy.Context.Remove("evidence_reference");
        }
        copy.ProjectToken = native ? ProjectToken(options) : null;
        copy = TelemetryPrivacy.ProtectEvent(copy, options.RedactFields);
        if (copy == null || HasTypedEvidence(copy) && !BeforeSendProcessor.IsValid(copy)) return null;
        if (!native || NativeBytes(copy) <= BoundedHttpResponse.MaxBytes - 13) return copy;
        foreach (var mark in new[] { true, false })
        {
            var reduced = DebugBundleClient.WithoutOptionalEvidence(copy, mark);
            var safe = reduced == null ? null : TelemetryPrivacy.ProtectEvent(reduced, options.RedactFields);
            if (safe != null && BeforeSendProcessor.IsValid(safe) && NativeBytes(safe) <= BoundedHttpResponse.MaxBytes - 13) return safe;
        }
        return null;
    }
    private static int NativeBytes(DebugBundleEventEnvelope envelope) => JsonSerializer.SerializeToUtf8Bytes(envelope, JsonSerialization.Options).Length;
    private static async Task<bool> DeliverAcceptedAsync(IReadOnlyList<DebugBundleEventEnvelope> accepted, DebugBundleOptions sdkOptions, DebugBundleRelayOptions relayOptions, EvidenceDeliveryState state, CancellationToken cancellationToken)
    {
        var token = ProjectToken(sdkOptions);
        if (sdkOptions.ProjectMode == DebugBundleProjectMode.LocalOnly)
            return (await SendWithFileTransportAsync(sdkOptions.LocalEventsDir, accepted, token, cancellationToken).ConfigureAwait(false)).Delivered;
        FileDeliveryResult? spool = null;
        if (relayOptions.DurableWrite)
        {
            spool = await SendWithFileTransportAsync(sdkOptions.SpoolDir, accepted, string.Empty, cancellationToken).ConfigureAwait(false);
            if (!spool.Delivered) return false;
        }
        var delivered = await ForwardAsync(accepted, sdkOptions, relayOptions, state, cancellationToken).ConfigureAwait(false);
        if (delivered.Verified) MarkDelivered(spool?.WrittenFilePath);
        return spool?.Delivered == true || delivered.Accepted;
    }
    private static async Task<FileDeliveryResult> SendWithFileTransportAsync(string root, IReadOnlyList<DebugBundleEventEnvelope> accepted, string token, CancellationToken cancellationToken)
    {
        try
        {
            var result = await new FileEventTransport(root).SendAsync(new EventTransportRequest { ProjectToken = token, Events = accepted }, cancellationToken).ConfigureAwait(false);
            return new FileDeliveryResult(result.StatusCode is >= 200 and < 300, result.WrittenFilePath);
        }
        catch { return new FileDeliveryResult(false, null); }
    }
    private static async Task<(bool Accepted, bool Verified)> ForwardAsync(IReadOnlyList<DebugBundleEventEnvelope> events, DebugBundleOptions options, DebugBundleRelayOptions relay, EvidenceDeliveryState state, CancellationToken cancellationToken)
    {
        var token = ProjectToken(options); if (string.IsNullOrWhiteSpace(token)) return (false, false);
        using var owned = relay.ForwardTransport == null ? new HttpEventTransport(options.Endpoint, options.RequestTimeout) : null;
        var transport = relay.ForwardTransport ?? owned!;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.RequestTimeout > TimeSpan.Zero && options.RequestTimeout < TimeSpan.FromSeconds(5) ? options.RequestTimeout : TimeSpan.FromSeconds(5));
        try { return await ForwardChunksAsync(events, options, transport, state, deadline.Token, false).ConfigureAwait(false); }
        catch { return (false, false); }
    }
    private static async Task<(bool Accepted, bool Verified)> ForwardChunksAsync(IReadOnlyList<DebugBundleEventEnvelope> events, DebugBundleOptions options, IEventTransport transport, EvidenceDeliveryState state, CancellationToken cancellationToken, bool retried)
    {
        var offset = 0; var verified = true; var native = transport is HttpEventTransport;
        while (offset < events.Count)
        {
            cancellationToken.ThrowIfCancellationRequested(); var chunk = new List<DebugBundleEventEnvelope>(); var bytes = 13;
            for (var index = offset; index < events.Count; index++)
            {
                var item = ForwardEvent(events[index], options, state, native); if (item == null) return (false, false);
                var next = NativeBytes(item) + (chunk.Count > 0 ? 1 : 0);
                if (native && bytes + next > BoundedHttpResponse.MaxBytes) break;
                chunk.Add(item); bytes += next;
            }
            if (chunk.Count == 0) return (false, false);
            var response = await SendForwardAsync(transport, new EventTransportRequest { ProjectToken = ProjectToken(options), Events = chunk }, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is < 200 or >= 300) return (false, false);
            var acknowledgement = IngestionAcknowledgement.Decide(response.Body, chunk.Count, native);
            var lost = state.ObserveBody(response.Body, native, acknowledgement.Kind == IngestionAcknowledgementKind.Acknowledged);
            if (acknowledgement.Kind == IngestionAcknowledgementKind.Legacy && !native) verified = false;
            else if (acknowledgement.Kind != IngestionAcknowledgementKind.Acknowledged) return (false, false);
            else if (acknowledgement.Accepted != chunk.Count)
            {
                if (retried || !lost || acknowledgement.RetryableIndices.Count > 0 || acknowledgement.TerminalErrors.Count != chunk.Count - acknowledgement.Accepted || acknowledgement.TerminalErrors.Any(error => error.Reason != "invalid_event")) return (false, false);
                var reduced = new List<DebugBundleEventEnvelope>();
                foreach (var error in acknowledgement.TerminalErrors)
                {
                    var original = chunk[error.Index]; if (original.EventType != "frontend_exception") return (false, false);
                    var next = ForwardEvent(original, options, state, native);
                    if (next == null || JsonSerializer.Serialize(next, JsonSerialization.Options) == JsonSerializer.Serialize(original, JsonSerialization.Options)) return (false, false);
                    reduced.Add(next);
                }
                var fallback = await ForwardChunksAsync(reduced, options, transport, state, cancellationToken, true).ConfigureAwait(false);
                if (!fallback.Verified) return (false, false);
            }
            offset += chunk.Count;
        }
        return (true, verified);
    }
    private static async Task<EventTransportResult> SendForwardAsync(IEventTransport transport, EventTransportRequest request, CancellationToken cancellationToken)
    {
        if (transport is HttpEventTransport) return await transport.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!CustomForwardSlot.Wait(0)) throw new IOException("relay forwarder busy");
        var task = Task.Run(async () => { try { return await transport.SendAsync(request, cancellationToken).ConfigureAwait(false); } finally { CustomForwardSlot.Release(); } });
        return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
    private static void MarkDelivered(string? spoolPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(spoolPath)) return;
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using var stream = new FileStream(spoolPath + DeliveredMarkerSuffix, options);
        }
        catch { /* Exclusive marker failure leaves a safely persisted spool pending. */ }
    }
}
