namespace DebugBundle;

public static class DebugBundle
{
    private static readonly object Sync = new();
    private static DebugBundleClient? _client;
    private static bool _taskSchedulerHooked;
    private static bool _appDomainHooked;
    private static bool _consoleErrorHooked;
    private static bool _consoleOutHooked;

    public static DebugBundleStatus Status => Current?.Status ?? DebugBundleStatus.Disconnected;
    public static DateTimeOffset? LastEventAt => Current?.LastEventAt;

    private static DebugBundleClient? Current
    {
        get
        {
            lock (Sync)
            {
                return _client;
            }
        }
    }

    public static DebugBundleClient Init(DebugBundleOptions options)
    {
        var client = DebugBundleClient.Create(options);
        lock (Sync)
        {
            _client?.Dispose();
            _client = client;
        }

        return client;
    }

    public static void CaptureException(Exception? exception, IDictionary<string, object?>? context = null)
        => Current?.CaptureException(exception, context);

    public static void CaptureError(Exception? exception, IDictionary<string, object?>? context = null)
        => Current?.CaptureError(exception, context);

    public static void CaptureLog(string? message, DebugBundleLogLevel level = DebugBundleLogLevel.Information, IDictionary<string, object?>? context = null)
        => Current?.CaptureLog(message, level, context);

    public static void CaptureRequest(DebugBundleRequestInfo? request, DebugBundleResponseInfo? response, IDictionary<string, object?>? context = null)
        => Current?.CaptureRequest(request, response, context);

    public static void CaptureMessage(string? message, DebugBundleLogLevel level = DebugBundleLogLevel.Information, IDictionary<string, object?>? context = null)
        => Current?.CaptureMessage(message, level, context);

    public static void SetContext(string key, object? value) => Current?.SetContext(key, value);
    public static void SetUserHash(string userHash) => Current?.SetUserHash(userHash);
    public static void SetTraceId(string traceId) => Current?.SetTraceId(traceId);
    public static void SetRequestId(string requestId) => Current?.SetRequestId(requestId);
    public static DebugBundleScope BeginScope(IDictionary<string, object?> values) => DebugBundleContext.BeginScope(values);
    public static void Probe(string label, object? data, ProbeOptions? options = null) => Current?.Probe(label, data, options);
    public static void Probe(string label, Func<object?> data, ProbeOptions? options = null) => Current?.Probe(label, data, options);
    public static Task FlushAsync(CancellationToken cancellationToken = default) => Current?.FlushAsync(cancellationToken) ?? Task.CompletedTask;

    public static void CaptureUnhandledExceptions()
    {
        CaptureAppDomainExceptions();
        CaptureTaskSchedulerExceptions();
    }

    public static void CaptureTaskSchedulerExceptions()
    {
        lock (Sync)
        {
            if (_taskSchedulerHooked)
            {
                return;
            }

            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
            _taskSchedulerHooked = true;
        }
    }

    public static void CaptureAppDomainExceptions()
    {
        lock (Sync)
        {
            if (_appDomainHooked)
            {
                return;
            }

            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            _appDomainHooked = true;
        }
    }

    public static void CaptureConsoleLogs(bool includeStandardOutput = false)
    {
        lock (Sync)
        {
            if (!_consoleErrorHooked)
            {
                Console.SetError(new DebugBundleConsoleWriter(Console.Error, DebugBundleLogLevel.Error, () => Current));
                _consoleErrorHooked = true;
            }

            if (includeStandardOutput && !_consoleOutHooked)
            {
                Console.SetOut(new DebugBundleConsoleWriter(Console.Out, DebugBundleLogLevel.Information, () => Current));
                _consoleOutHooked = true;
            }
        }
    }

    public static void WithExceptionCapture(Action action, IDictionary<string, object?>? context = null)
    {
        if (action == null)
        {
            return;
        }

        try
        {
            action();
        }
        catch (Exception exception)
        {
            CaptureException(exception, context);
            throw;
        }
    }

    public static T WithExceptionCapture<T>(Func<T> action, IDictionary<string, object?>? context = null)
    {
        if (action == null)
        {
            return default!;
        }

        try
        {
            return action();
        }
        catch (Exception exception)
        {
            CaptureException(exception, context);
            throw;
        }
    }

    public static async Task WithExceptionCaptureAsync(Func<Task> action, IDictionary<string, object?>? context = null)
    {
        if (action == null)
        {
            return;
        }

        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            CaptureException(exception, context);
            throw;
        }
    }

    public static async Task<T> WithExceptionCaptureAsync<T>(Func<Task<T>> action, IDictionary<string, object?>? context = null)
    {
        if (action == null)
        {
            return default!;
        }

        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            CaptureException(exception, context);
            throw;
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        CaptureException(args.Exception, new Dictionary<string, object?> { ["source"] = "task_scheduler_unobserved" });
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        if (args.ExceptionObject is Exception exception)
        {
            CaptureException(exception, new Dictionary<string, object?>
            {
                ["source"] = "app_domain_unhandled",
                ["is_terminating"] = args.IsTerminating
            });
        }
    }
}
