using DebugBundle;
using DebugBundle.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DebugBundle.Worker.Tests;

public sealed class WorkerTests
{
    [Fact]
    public async Task CaptureOperationAsync_Captures_Context_And_Rethrows()
    {
        var client = new FakeClient();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.CaptureOperationAsync("billing.reconcile", (context, _) =>
            {
                context.Set("tenant_id", "tenant_123");
                throw new InvalidOperationException("reconcile failed");
            }));

        Assert.Equal("reconcile failed", thrown.Message);
        var captured = Assert.Single(client.Exceptions);
        Assert.Equal("billing.reconcile", captured.Context!["operation"]);
        Assert.Equal("tenant_123", captured.Context!["tenant_id"]);
    }

    [Fact]
    public async Task CaptureOperationAsync_Preserves_Cancellation_Without_Capture()
    {
        var client = new FakeClient();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            client.CaptureOperationAsync("billing.reconcile", (_, token) => throw new OperationCanceledException(token), cts.Token));

        Assert.Empty(client.Exceptions);
    }

    [Fact]
    public async Task Worker_Service_Registration_Flushes_On_Stop()
    {
        var fakeClient = new FakeClient();
        var services = new ServiceCollection();
        services.AddSingleton<IDebugBundleClient>(fakeClient);
        services.AddSingleton<IHostApplicationLifetime>(new FakeHostApplicationLifetime());
        services.AddDebugBundleWorkerCapture();

        using var provider = services.BuildServiceProvider();
        var hostedService = Assert.Single(provider.GetServices<IHostedService>());

        await hostedService.StopAsync(CancellationToken.None);

        Assert.Equal(1, fakeClient.FlushCount);
    }
}
