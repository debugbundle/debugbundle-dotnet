using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace DebugBundle.Logging;

// Framework templates are formatted only from bounded primitive copies. Application
// formatters and reference state are weakly held until the existing sender consumes them.
internal static class LogProjection
{
    internal const string Unavailable = "Log details unavailable";
    internal static bool IsFrameworkState(object state) =>
        state.GetType().Assembly == typeof(ILogger).Assembly &&
        state is IReadOnlyList<KeyValuePair<string, object?>>;

    internal static object? Primitive(object? value) => value switch
    {
        null => null,
        string text => text.Length <= 4096 ? text : text.Substring(0, 4096),
        bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal or char => value,
        DateTime or DateTimeOffset or Guid or TimeSpan => value,
        _ => "[Unsupported value]"
    };

    internal static string Snapshot<TState>(TState state, Exception? exception)
    {
        if (state is string text) return (string)Primitive(text)!;
        if (state != null && IsFrameworkState(state))
        {
            var values = (IReadOnlyList<KeyValuePair<string, object?>>)(object)state;
            if (values.Count > 0 && values.Count <= 51 && values[values.Count - 1].Value is string template && template.Length <= 4096 && HasBoundedFormats(template))
            {
                var args = new object?[values.Count - 1];
                for (var index = 0; index < args.Length; index++) args[index] = Primitive(values[index].Value);
                var renderer = new TemplateRenderer();
                // LoggerExtensions supplies its own formatter; no host callback runs here.
                renderer.Log(LogLevel.Error, template, args);
                return renderer.Message;
            }
        }
        return exception == null ? Unavailable : ExceptionProjection.Message(exception);
    }

    private static bool HasBoundedFormats(string template)
    {
        // Alignment and numeric precision can otherwise expand a tiny template to GBs.
        foreach (Match match in Regex.Matches(template, @",\s*[+-]?(\d+)|:[a-zA-Z](\d+)", RegexOptions.CultureInvariant))
        {
            var digits = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            if (!int.TryParse(digits, out var size) || size > 256) return false;
        }
        return true;
    }

    internal static Action<DebugBundleEventEnvelope>? Defer<TState>(TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (state == null || typeof(TState).IsValueType) return null;
        return new Deferred<TState>(state, exception, formatter).Apply;
    }

    private sealed class Deferred<TState>
    {
        private readonly WeakReference<object> _state;
        private readonly WeakReference<Func<TState, Exception?, string>> _formatter;
        private readonly WeakReference<Exception>? _exception;
        internal Deferred(TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _state = new WeakReference<object>(state!);
            _formatter = new WeakReference<Func<TState, Exception?, string>>(formatter);
            _exception = exception == null ? null : new WeakReference<Exception>(exception);
        }
        internal void Apply(DebugBundleEventEnvelope envelope)
        {
            if (!_state.TryGetTarget(out var state) || !_formatter.TryGetTarget(out var formatter)) return;
            Exception? exception = null;
            _exception?.TryGetTarget(out exception);
            var message = formatter((TState)state, exception);
            if (string.IsNullOrWhiteSpace(message)) return;
            envelope.Payload["message"] = message;
            if (envelope.Payload.TryGetValue("attributes", out var fields) && fields is Dictionary<string, object?> attributes)
                attributes["message"] = message;
        }
    }

    private sealed class TemplateRenderer : ILogger
    {
        internal string Message = Unavailable;
        public bool IsEnabled(LogLevel level) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Message = formatter(state, exception);
    }
}
