using DebugBundle;
using DebugBundle.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DebugBundle.Extensions.Logging.Tests;

public sealed class LoggingProviderTests
{
    [Fact]
    public void Logger_Captures_Structured_State_Scopes_And_Exception_Summary()
    {
        var fakeClient = new FakeClient();
        using var provider = BuildProvider(fakeClient);
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Checkout.Payment");
        using (logger.BeginScope(new Dictionary<string, object?> { ["request_id"] = "req_123", ["tenant_id"] = "tenant_123" }))
        {
            logger.LogError(new InvalidOperationException("card failed"), "Payment retry failed for order {OrderId}", "ord_123");
        }

        var captured = Assert.Single(fakeClient.Logs);
        Assert.Equal(DebugBundleLogLevel.Error, captured.Level);
        Assert.Equal("Payment retry failed for order ord_123", captured.Message);
        Assert.NotNull(captured.Context);
        var context = captured.Context!;
        Assert.Equal("Checkout.Payment", context["logger.category"]);
        Assert.Equal("Payment retry failed for order {OrderId}", context["message_template"]);
        var structured = Assert.IsType<Dictionary<string, object?>>(context["structured_state"]);
        Assert.Equal("ord_123", structured["OrderId"]);
        var scopes = Assert.IsType<List<object?>>(context["scopes"]);
        var scope = Assert.IsType<Dictionary<string, object?>>(Assert.Single(scopes));
        Assert.Equal("req_123", scope["request_id"]);
        var exception = Assert.IsType<Dictionary<string, object?>>(context["exception"]);
        Assert.Equal(typeof(InvalidOperationException).FullName, exception["type"]);
    }

    [Fact]
    public void Logger_Respects_Minimum_Level_And_Excluded_Sdk_Category()
    {
        var fakeClient = new FakeClient();
        using var provider = BuildProvider(fakeClient);
        var factory = provider.GetRequiredService<ILoggerFactory>();

        factory.CreateLogger("Checkout.Payment").LogInformation("not captured");
        factory.CreateLogger("DebugBundle.Transport").LogError("not recursive");
        factory.CreateLogger("Checkout.Payment").LogWarning("captured warning");

        var captured = Assert.Single(fakeClient.Logs);
        Assert.Equal("captured warning", captured.Message);
        Assert.Equal(DebugBundleLogLevel.Warning, captured.Level);
    }

    [Fact]
    public void Logger_Degrades_When_Client_Is_Not_Registered()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddDebugBundle());

        using var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Checkout.Payment");

        logger.LogError("missing client must not throw");
    }

    private static ServiceProvider BuildProvider(FakeClient fakeClient)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDebugBundleClient>(fakeClient);
        services.AddLogging(builder => builder.AddDebugBundle(options => options.MinimumLevel = LogLevel.Warning));
        return services.BuildServiceProvider();
    }
}
