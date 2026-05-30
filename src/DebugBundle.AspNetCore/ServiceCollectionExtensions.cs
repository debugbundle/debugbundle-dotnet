using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace DebugBundle.AspNetCore;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDebugBundle(this IServiceCollection services, Action<DebugBundleOptions> configure)
    {
        services.Configure(configure);
        return services.AddDebugBundleCore();
    }

    public static IServiceCollection AddDebugBundle(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DebugBundleOptions>(configuration);
        return services.AddDebugBundleCore();
    }

    public static IServiceCollection AddDebugBundleBlazorServer(this IServiceCollection services)
    {
        services.AddScoped<CircuitHandler, DebugBundleCircuitHandler>();
        return services;
    }

    private static IServiceCollection AddDebugBundleCore(this IServiceCollection services)
    {
        services.AddSingleton<IDebugBundleClient>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<DebugBundleOptions>>().Value;
            return DebugBundleClient.Create(options);
        });
        services.AddHostedService<DebugBundleFlushHostedService>();
        return services;
    }
}

internal sealed class DebugBundleFlushHostedService : IHostedService
{
    private readonly IDebugBundleClient _client;
    private readonly IHostApplicationLifetime _lifetime;

    public DebugBundleFlushHostedService(IDebugBundleClient client, IHostApplicationLifetime lifetime)
    {
        _client = client;
        _lifetime = lifetime;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _lifetime.ApplicationStopping.Register(() =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            _client.FlushAsync(cts.Token).GetAwaiter().GetResult();
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => _client.FlushAsync(cancellationToken);
}
