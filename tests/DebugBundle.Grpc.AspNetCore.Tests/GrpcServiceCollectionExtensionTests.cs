using DebugBundle.Grpc;
using Grpc.AspNetCore.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DebugBundle.Grpc.AspNetCore.Tests;

public sealed class GrpcServiceCollectionExtensionTests
{
    [Fact]
    public void AddDebugBundleInterceptor_Registers_The_Server_Interceptor()
    {
        var services = new ServiceCollection();
        var builder = services.AddGrpc();

        Assert.Same(builder, builder.AddDebugBundleInterceptor());

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<GrpcServiceOptions>>().Value;
        Assert.Contains(options.Interceptors, registration => registration.Type == typeof(DebugBundleGrpcInterceptor));
    }
}
