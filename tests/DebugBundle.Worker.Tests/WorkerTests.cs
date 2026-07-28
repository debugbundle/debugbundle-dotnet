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

    [Fact]
    public async Task AddDebugBundle_Registers_Configured_Client_And_Flushes_When_Stopping()
    {
        var lifetime = new FakeHostApplicationLifetime();
        var services = new ServiceCollection();
        services.AddSingleton<IHostApplicationLifetime>(lifetime);
        services.AddDebugBundle(options =>
        {
            options.Enabled = false;
            options.ProjectToken = "dbundle_proj_test";
            options.Service = "worker-test";
            options.Environment = "test";
        });

        using var provider = services.BuildServiceProvider();
        Assert.IsType<DebugBundleClient>(provider.GetRequiredService<IDebugBundleClient>());
        var hostedService = Assert.Single(provider.GetServices<IHostedService>());

        await hostedService.StartAsync(CancellationToken.None);
        lifetime.StopApplication();
        await hostedService.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CaptureOperationAsync_Covers_Success_Null_And_Generic_Paths()
    {
        var client = new FakeClient();
        await client.CaptureOperationAsync("success", (context, _) =>
        {
            context.Set("", "ignored");
            context.Set("temporary", "value");
            context.Set("temporary", null);
            Assert.Equal("success", context.OperationName);
            Assert.DoesNotContain("temporary", context.Snapshot());
            return Task.CompletedTask;
        });
        await client.CaptureOperationAsync("null", null!);
        Assert.Equal(42, await client.CaptureOperationAsync("generic", (_, _) => Task.FromResult(42)));
        Assert.Equal(0, await client.CaptureOperationAsync<int>("generic-null", null!));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.CaptureOperationAsync<int>(
                "generic-failure",
                (_, _) => throw new InvalidOperationException("failed")));

        Assert.Single(client.Exceptions);
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            DebugBundleWorkerClientExtensions.CaptureOperationAsync(
                null!,
                "null-client",
                (_, _) => Task.CompletedTask));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            DebugBundleWorkerClientExtensions.CaptureOperationAsync<int>(
                null!,
                "null-client",
                (_, _) => Task.FromResult(1)));
    }
}
