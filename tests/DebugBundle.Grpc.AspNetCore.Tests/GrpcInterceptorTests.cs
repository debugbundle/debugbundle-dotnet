using DebugBundle.Grpc;
using Grpc.Core;

namespace DebugBundle.Grpc.AspNetCore.Tests;

public sealed class GrpcInterceptorTests
{
    [Fact]
    public async Task Unary_Handler_Captures_Success_Metadata()
    {
        var client = new FakeClient();
        var interceptor = new DebugBundleGrpcInterceptor(client);
        var context = new TestServerCallContext("/checkout.Payment/Authorize", new Metadata
        {
            { "x-debugbundle-trace-id", "trace_grpc" },
            { "x-request-id", "req_grpc" }
        });

        var response = await interceptor.UnaryServerHandler("request", context, (_, _) => Task.FromResult("response"));

        Assert.Equal("response", response);
        var captured = Assert.Single(client.Requests);
        Assert.Equal("GRPC", captured.Request.Method);
        Assert.Equal("/checkout.Payment/Authorize", captured.Request.Path);
        Assert.Equal("trace_grpc", captured.Context!["trace_id"]);
        Assert.Equal(200, captured.Response!.StatusCode);
    }

    [Fact]
    public async Task Unary_Handler_Captures_Exception_And_Rethrows()
    {
        var client = new FakeClient();
        var interceptor = new DebugBundleGrpcInterceptor(client);
        var context = new TestServerCallContext("/checkout.Payment/Authorize");

        await Assert.ThrowsAsync<RpcException>(() =>
            interceptor.UnaryServerHandler<string, string>(
                "request",
                context,
                (_, _) => throw new RpcException(new Status(StatusCode.PermissionDenied, "denied"))));

        var capturedException = Assert.Single(client.Exceptions);
        Assert.Equal(StatusCode.PermissionDenied.ToString(), capturedException.Context!["grpc.status_code"]);
        var capturedRequest = Assert.Single(client.Requests);
        Assert.Equal(500, capturedRequest.Response!.StatusCode);
    }

    [Fact]
    public async Task Streaming_Handlers_Capture_Success_And_Failure_Paths()
    {
        var client = new FakeClient();
        var interceptor = new DebugBundleGrpcInterceptor(client);
        var reader = new StubAsyncStreamReader<string>();
        var writer = new StubServerStreamWriter<string>();

        var clientResponse = await interceptor.ClientStreamingServerHandler(
            reader,
            new TestServerCallContext("/stream/client"),
            (_, _) => Task.FromResult("response"));
        Assert.Equal("response", clientResponse);
        await Assert.ThrowsAsync<RpcException>(() =>
            interceptor.ClientStreamingServerHandler<string, string>(
                reader,
                new TestServerCallContext("/stream/client-error"),
                (_, _) => throw new RpcException(new Status(StatusCode.Aborted, "aborted"))));

        await interceptor.ServerStreamingServerHandler(
            "request",
            writer,
            new TestServerCallContext("/stream/server"),
            (_, _, _) => Task.CompletedTask);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            interceptor.ServerStreamingServerHandler(
                "request",
                writer,
                new TestServerCallContext("/stream/server-error"),
                (_, _, _) => throw new InvalidOperationException("failed")));

        await interceptor.DuplexStreamingServerHandler(
            reader,
            writer,
            new TestServerCallContext("/stream/duplex"),
            (_, _, _) => Task.CompletedTask);
        await Assert.ThrowsAsync<RpcException>(() =>
            interceptor.DuplexStreamingServerHandler<string, string>(
                reader,
                writer,
                new TestServerCallContext("/stream/duplex-error"),
                (_, _, _) => throw new RpcException(new Status(StatusCode.Internal, "failed"))));

        Assert.Equal(6, client.Requests.Count);
        Assert.Equal(3, client.Exceptions.Count);
    }

    [Fact]
    public async Task Unary_Handler_Maps_NonRpc_Exception_To_Unknown()
    {
        var client = new FakeClient();
        var interceptor = new DebugBundleGrpcInterceptor(client);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            interceptor.UnaryServerHandler<string, string>(
                "request",
                new TestServerCallContext("/checkout.Payment/Authorize"),
                (_, _) => throw new InvalidOperationException("failed")));

        Assert.Equal(StatusCode.Unknown.ToString(), client.Exceptions.Single().Context!["grpc.status_code"]);
    }

    private sealed class StubAsyncStreamReader<T> : IAsyncStreamReader<T>
    {
        public T Current => default!;
        public Task<bool> MoveNext(CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class StubServerStreamWriter<T> : IServerStreamWriter<T>
    {
        public WriteOptions? WriteOptions { get; set; }
        public Task WriteAsync(T message) => Task.CompletedTask;
    }
}
