namespace DebugBundle;

// Shared by the native logger adapters without extending IDebugBundleClient.
internal static class LoggerExceptionCapture
{
    internal static bool Eligible(IDebugBundleClient client, DebugBundleLogLevel level) => client switch
    {
        DebugBundleClient concrete => concrete.CanAdmitLog(level),
        StaticDebugBundleClient => DebugBundle.CanAdmitLog(level),
        _ => true
    };

    internal static void Capture(IDebugBundleClient client, Exception error, DebugBundleLogLevel level,
        IDictionary<string, object?> context)
    {
        if (Eligible(client, level)) client.CaptureException(error, context);
    }
}
