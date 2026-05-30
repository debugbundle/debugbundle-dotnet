using Grpc.Core;

namespace DebugBundle.Grpc.AspNetCore.Tests;

internal sealed class TestServerCallContext : ServerCallContext
{
    private readonly Metadata _requestHeaders;
    private readonly Metadata _responseTrailers = new();
    private Status _status;
    private WriteOptions? _writeOptions;

    public TestServerCallContext(string method, Metadata? requestHeaders = null)
    {
        MethodValue = method;
        _requestHeaders = requestHeaders ?? new Metadata();
    }

    public string MethodValue { get; }

    protected override string MethodCore => MethodValue;
    protected override string HostCore => "localhost";
    protected override string PeerCore => "ipv4:127.0.0.1:5000";
    protected override DateTime DeadlineCore => DateTime.UtcNow.AddMinutes(1);
    protected override Metadata RequestHeadersCore => _requestHeaders;
    protected override CancellationToken CancellationTokenCore => CancellationToken.None;
    protected override Metadata ResponseTrailersCore => _responseTrailers;
    protected override Status StatusCore { get => _status; set => _status = value; }
    protected override WriteOptions? WriteOptionsCore { get => _writeOptions; set => _writeOptions = value; }
    protected override AuthContext AuthContextCore => new("anonymous", new Dictionary<string, List<AuthProperty>>());

    protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options)
    {
        throw new NotSupportedException();
    }

    protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders)
    {
        return Task.CompletedTask;
    }
}
