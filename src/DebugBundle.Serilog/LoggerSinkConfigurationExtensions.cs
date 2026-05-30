using Serilog;
using Serilog.Configuration;

namespace DebugBundle.Serilog;

public static class LoggerSinkConfigurationExtensions
{
    public static LoggerConfiguration DebugBundle(this LoggerSinkConfiguration sinks)
    {
        if (sinks == null)
        {
            throw new ArgumentNullException(nameof(sinks));
        }

        return sinks.Sink(new DebugBundleSink());
    }

    public static LoggerConfiguration DebugBundle(this LoggerSinkConfiguration sinks, IDebugBundleClient client)
    {
        if (sinks == null)
        {
            throw new ArgumentNullException(nameof(sinks));
        }

        return sinks.Sink(new DebugBundleSink(client));
    }
}
