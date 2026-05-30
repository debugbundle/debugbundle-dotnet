namespace DebugBundle.Sdk.Tests;

internal sealed class FakeRemoteConfigFetcher : IRemoteConfigFetcher
{
    public List<RemoteConfigFetchRequest> Requests { get; } = new();
    public Queue<Func<RemoteConfigFetchRequest, RemoteConfigFetchResult>> Responses { get; } = new();

    public Task<RemoteConfigFetchResult> FetchAsync(RemoteConfigFetchRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        if (Responses.Count == 0)
        {
            return Task.FromResult(new RemoteConfigFetchResult { Config = SdkRemoteConfig.Balanced(), ETag = "\"default\"" });
        }

        return Task.FromResult(Responses.Dequeue()(request));
    }
}
