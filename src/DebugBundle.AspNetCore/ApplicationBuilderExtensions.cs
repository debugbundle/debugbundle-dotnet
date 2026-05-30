using DebugBundle.AspNetCore.Relay;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DebugBundle.AspNetCore;

public static class ApplicationBuilderExtensions
{
    public static IApplicationBuilder UseDebugBundle(this IApplicationBuilder app)
        => app.UseMiddleware<DebugBundleMiddleware>();

    public static IApplicationBuilder UseDebugBundleBrowserRelay(this IApplicationBuilder app, string path = "/debugbundle/browser")
    {
        return app.Use(async (context, next) =>
        {
            if (!context.Request.Path.Equals(path, StringComparison.Ordinal))
            {
                await next().ConfigureAwait(false);
                return;
            }

            var client = context.RequestServices.GetService<IDebugBundleClient>();
            var sdkOptions = context.RequestServices.GetRequiredService<IOptions<DebugBundleOptions>>().Value;
            var relayOptions = context.RequestServices.GetService<IOptions<DebugBundleRelayOptions>>()?.Value ?? new DebugBundleRelayOptions();
            await DebugBundleRelayHandler.HandleAsync(context, sdkOptions, relayOptions, client, context.RequestAborted).ConfigureAwait(false);
        });
    }

    public static IEndpointConventionBuilder MapDebugBundleBrowserRelay(this IEndpointRouteBuilder endpoints, string pattern = "/debugbundle/browser")
    {
        return endpoints.MapMethods(pattern, new[] { HttpMethods.Post, HttpMethods.Options }, async context =>
        {
            var client = context.RequestServices.GetService<IDebugBundleClient>();
            var sdkOptions = context.RequestServices.GetRequiredService<IOptions<DebugBundleOptions>>().Value;
            var relayOptions = context.RequestServices.GetService<IOptions<DebugBundleRelayOptions>>()?.Value ?? new DebugBundleRelayOptions();
            await DebugBundleRelayHandler.HandleAsync(context, sdkOptions, relayOptions, client, context.RequestAborted).ConfigureAwait(false);
        });
    }
}
