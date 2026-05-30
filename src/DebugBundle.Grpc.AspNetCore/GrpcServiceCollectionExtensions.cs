using Grpc.AspNetCore.Server;
using Microsoft.Extensions.DependencyInjection;

namespace DebugBundle.Grpc;

public static class GrpcServiceCollectionExtensions
{
    public static IGrpcServerBuilder AddDebugBundleInterceptor(this IGrpcServerBuilder builder)
    {
        builder.Services.Configure<GrpcServiceOptions>(options => options.Interceptors.Add<DebugBundleGrpcInterceptor>());
        return builder;
    }
}
