using DebugBundle;
using DebugBundle.Transport;

namespace DebugBundle.Sdk.Tests;

internal sealed class FakeTransport : IEventTransport
{
    private readonly Queue<EventTransportResult> _responses = new();

    public List<IReadOnlyList<DebugBundleEventEnvelope>> Batches { get; } = new();
    public int Calls { get; private set; }

    public void EnqueueResponse(EventTransportResult response) => _responses.Enqueue(response);

    public Task<EventTransportResult> SendAsync(EventTransportRequest request, CancellationToken cancellationToken = default)
    {
        Calls++;
        Batches.Add(request.Events.ToArray());
        return Task.FromResult(_responses.Count > 0 ? _responses.Dequeue() : new EventTransportResult { StatusCode = 202 });
    }
}
