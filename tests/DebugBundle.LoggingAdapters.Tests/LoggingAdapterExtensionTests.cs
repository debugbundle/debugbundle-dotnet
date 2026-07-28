using DebugBundle.Serilog;
using Serilog;

namespace DebugBundle.LoggingAdapters.Tests;

public sealed class LoggingAdapterExtensionTests
{
    [Fact]
    public void Serilog_Extensions_Register_Default_And_Explicit_Client_Sinks()
    {
        using var explicitLogger = new LoggerConfiguration()
            .WriteTo.DebugBundle(new FakeClient())
            .CreateLogger();
        using var defaultLogger = new LoggerConfiguration()
            .WriteTo.DebugBundle()
            .CreateLogger();

        explicitLogger.Information("explicit adapter");
        defaultLogger.Information("default adapter");
    }

    [Fact]
    public void Serilog_Extensions_Reject_Null_Sink_Configuration()
    {
        Assert.Throws<ArgumentNullException>(() =>
            LoggerSinkConfigurationExtensions.DebugBundle(null!));
        Assert.Throws<ArgumentNullException>(() =>
            LoggerSinkConfigurationExtensions.DebugBundle(null!, new FakeClient()));
    }
}
