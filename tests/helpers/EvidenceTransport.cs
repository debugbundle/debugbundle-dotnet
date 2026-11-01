using DebugBundle;
using DebugBundle.Transport;

internal sealed class EvidenceTransport : IEventTransport
{
    internal readonly List<DebugBundleEventEnvelope> Events = new();
    public Task<EventTransportResult> SendAsync(EventTransportRequest request, CancellationToken cancellationToken = default)
    {
        Events.AddRange(request.Events);
        return Task.FromResult(new EventTransportResult { StatusCode = 202, Body = "{\"accepted\":" + request.Events.Count + ",\"rejected\":0,\"errors\":[]}" });
    }
}
