using DebugBundle;
using DebugBundle.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DebugBundle.Extensions.Logging.Tests;

public sealed class LoggingBuilderExtensionTests
{
    [Fact]
    public void Client_Overload_Registers_The_Client_And_Provider()
    {
        var client = new FakeClient();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddDebugBundle(client));

        using var provider = services.BuildServiceProvider();

        Assert.Same(client, provider.GetRequiredService<IDebugBundleClient>());
        Assert.Contains(provider.GetServices<ILoggerProvider>(), item => item is DebugBundleLoggerProvider);
    }
}
