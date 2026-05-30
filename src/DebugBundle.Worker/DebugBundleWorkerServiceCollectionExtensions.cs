using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DebugBundle.Worker;

public static class DebugBundleWorkerServiceCollectionExtensions
{
    public static IServiceCollection AddDebugBundle(this IServiceCollection services, Action<DebugBundleOptions> configure)
    {
        services.Configure(configure);
        return services.AddDebugBundleWorkerCapture();
    }

    public static IServiceCollection AddDebugBundle(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DebugBundleOptions>(configuration);
        return services.AddDebugBundleWorkerCapture();
    }

    public static IServiceCollection AddDebugBundleWorkerCapture(this IServiceCollection services)
    {
        services.TryAddSingleton<IDebugBundleClient>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<DebugBundleOptions>>().Value;
            return DebugBundleClient.Create(options);
        });
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, DebugBundleWorkerFlushHostedService>());
        return services;
    }
}

internal sealed class DebugBundleWorkerFlushHostedService : IHostedService
{
    private readonly IDebugBundleClient _client;
    private readonly IHostApplicationLifetime _lifetime;

    public DebugBundleWorkerFlushHostedService(IDebugBundleClient client, IHostApplicationLifetime lifetime)
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
