using NLog;
using NLog.Targets;

namespace DebugBundle.NLog;

[Target("DebugBundle")]
public class DebugBundleTarget : Target
{
    private readonly IDebugBundleClient _client;

    public DebugBundleTarget()
        : this(new StaticDebugBundleClient())
    {
    }

    public DebugBundleTarget(IDebugBundleClient client)
    {
        _client = client;
    }

    protected override void Write(LogEventInfo logEvent)
    {
        if (logEvent == null)
        {
            return;
        }

        try
        {
            if (!LoggerExceptionCapture.Eligible(_client, MapLevel(logEvent.Level))) return;
            var context = BuildContext(logEvent);
            if (logEvent.Exception != null)
            {
                context["log_message"] = logEvent.FormattedMessage;
                LoggerExceptionCapture.Capture(_client, logEvent.Exception, MapLevel(logEvent.Level), context);
                return;
            }

            _client.CaptureLog(logEvent.FormattedMessage, MapLevel(logEvent.Level), context);
        }
        catch
        {
        }
    }

    private static Dictionary<string, object?> BuildContext(LogEventInfo logEvent)
    {
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in logEvent.Properties)
        {
            if (property.Key != null)
            {
                properties[property.Key.ToString()!] = property.Value;
            }
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["logger"] = logEvent.LoggerName,
            ["provider"] = "nlog",
            ["level"] = logEvent.Level.Name,
            ["message_template"] = logEvent.Message,
            ["properties"] = properties,
            ["exception_type"] = logEvent.Exception?.GetType().FullName,
            ["exception_message"] = logEvent.Exception?.Message
        };
    }

    private static DebugBundleLogLevel MapLevel(LogLevel level)
    {
        if (level == LogLevel.Trace)
        {
            return DebugBundleLogLevel.Trace;
        }

        if (level == LogLevel.Debug)
        {
            return DebugBundleLogLevel.Debug;
        }

        if (level == LogLevel.Info)
        {
            return DebugBundleLogLevel.Information;
        }

        if (level == LogLevel.Warn)
        {
            return DebugBundleLogLevel.Warning;
        }

        return level == LogLevel.Fatal ? DebugBundleLogLevel.Critical : DebugBundleLogLevel.Error;
    }

}
