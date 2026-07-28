using DebugBundle.AspNetCore;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DebugBundle.AspNetCore.Tests;

public sealed class ServiceCollectionExtensionTests
{
    [Fact]
    public async Task AddDebugBundle_Registers_Client_Flush_Service_And_Blazor_Handler()
    {
        var lifetime = new TestHostApplicationLifetime();
        var services = new ServiceCollection();
        services.AddSingleton<IHostApplicationLifetime>(lifetime);
        services.AddDebugBundle(options =>
        {
            options.Enabled = false;
            options.ProjectToken = "dbundle_proj_test";
            options.Service = "aspnet-test";
            options.Environment = "test";
        });
        services.AddDebugBundleBlazorServer();

        using var provider = services.BuildServiceProvider();
        Assert.IsType<DebugBundleClient>(provider.GetRequiredService<IDebugBundleClient>());
        Assert.IsType<DebugBundleCircuitHandler>(
            provider.CreateScope().ServiceProvider.GetRequiredService<CircuitHandler>());
        var hostedService = Assert.Single(provider.GetServices<IHostedService>());

        await hostedService.StartAsync(CancellationToken.None);
        lifetime.StopApplication();
        await hostedService.StopAsync(CancellationToken.None);
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;

        public void StopApplication() => _stopping.Cancel();
    }
}
