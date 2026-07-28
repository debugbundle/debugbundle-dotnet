using Serilog.Core;
using Serilog.Events;

namespace DebugBundle.Serilog;

public sealed class DebugBundleSink : ILogEventSink
{
    private readonly IDebugBundleClient _client;

    public DebugBundleSink()
        : this(new StaticDebugBundleClient())
    {
    }

    public DebugBundleSink(IDebugBundleClient client)
    {
        _client = client;
    }

    public void Emit(LogEvent logEvent)
    {
        if (logEvent == null)
        {
            return;
        }

        try
        {
            var context = BuildContext(logEvent);
            if (logEvent.Exception != null)
            {
                _client.CaptureException(logEvent.Exception, context);
            }

            _client.CaptureLog(logEvent.RenderMessage(), MapLevel(logEvent.Level), context);
        }
        catch
        {
        }
    }

    private static Dictionary<string, object?> BuildContext(LogEvent logEvent)
    {
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in logEvent.Properties)
        {
            properties[property.Key] = property.Value.ToString();
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["logger"] = "serilog",
            ["message_template"] = logEvent.MessageTemplate.Text,
            ["timestamp"] = logEvent.Timestamp.ToString("O"),
            ["properties"] = properties,
            ["exception_type"] = logEvent.Exception?.GetType().FullName,
            ["exception_message"] = logEvent.Exception?.Message
        };
    }

    private static DebugBundleLogLevel MapLevel(LogEventLevel level)
    {
        return level switch
        {
            LogEventLevel.Verbose => DebugBundleLogLevel.Trace,
            LogEventLevel.Debug => DebugBundleLogLevel.Debug,
            LogEventLevel.Information => DebugBundleLogLevel.Information,
            LogEventLevel.Warning => DebugBundleLogLevel.Warning,
            LogEventLevel.Error => DebugBundleLogLevel.Error,
            LogEventLevel.Fatal => DebugBundleLogLevel.Critical,
            _ => DebugBundleLogLevel.Information
        };
    }

}
