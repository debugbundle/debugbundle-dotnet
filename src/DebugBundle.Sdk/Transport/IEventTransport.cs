namespace DebugBundle.Transport;

public interface IEventTransport
{
    Task<EventTransportResult> SendAsync(EventTransportRequest request, CancellationToken cancellationToken = default);
}

public sealed class EventTransportRequest
{
    public string ProjectToken { get; set; } = string.Empty;
    public IReadOnlyList<DebugBundleEventEnvelope> Events { get; set; } = Array.Empty<DebugBundleEventEnvelope>();
}

public sealed class EventTransportResult
{
    public int StatusCode { get; set; }
    public TimeSpan? RetryAfter { get; set; }
    public string? WrittenFilePath { get; set; }
}
