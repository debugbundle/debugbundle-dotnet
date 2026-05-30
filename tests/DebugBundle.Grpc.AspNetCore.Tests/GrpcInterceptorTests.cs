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
}
