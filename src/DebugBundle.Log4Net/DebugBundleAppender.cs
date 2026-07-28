using log4net.Appender;
using log4net.Core;

namespace DebugBundle.Log4Net;

public sealed class DebugBundleAppender : AppenderSkeleton
{
    private readonly IDebugBundleClient _client;

    public DebugBundleAppender()
        : this(new StaticDebugBundleClient())
    {
    }

    public DebugBundleAppender(IDebugBundleClient client)
    {
        _client = client;
    }

    protected override void Append(LoggingEvent loggingEvent)
    {
        if (loggingEvent == null)
        {
            return;
        }

        try
        {
            var context = BuildContext(loggingEvent);
            if (loggingEvent.ExceptionObject != null)
            {
                _client.CaptureException(loggingEvent.ExceptionObject, context);
            }

            _client.CaptureLog(loggingEvent.RenderedMessage, MapLevel(loggingEvent.Level), context);
        }
        catch
        {
        }
    }

    private static Dictionary<string, object?> BuildContext(LoggingEvent loggingEvent)
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["logger"] = loggingEvent.LoggerName,
            ["provider"] = "log4net",
            ["level"] = loggingEvent.Level?.DisplayName,
            ["thread"] = loggingEvent.ThreadName,
            ["exception_type"] = loggingEvent.ExceptionObject?.GetType().FullName,
            ["exception_message"] = loggingEvent.ExceptionObject?.Message
        };
    }

    private static DebugBundleLogLevel MapLevel(Level? level)
    {
        if (level == null)
        {
            return DebugBundleLogLevel.Information;
        }

        if (level >= Level.Fatal)
        {
            return DebugBundleLogLevel.Critical;
        }

        if (level >= Level.Error)
        {
            return DebugBundleLogLevel.Error;
        }

        if (level >= Level.Warn)
        {
            return DebugBundleLogLevel.Warning;
        }

        if (level >= Level.Info)
        {
            return DebugBundleLogLevel.Information;
        }

        return level >= Level.Debug ? DebugBundleLogLevel.Debug : DebugBundleLogLevel.Trace;
    }

}
