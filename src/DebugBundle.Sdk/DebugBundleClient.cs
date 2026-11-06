using System.Diagnostics;
using DebugBundle.Redaction;
using DebugBundle.Transport;

namespace DebugBundle;

public sealed partial class DebugBundleClient : IDebugBundleClient, IDebugBundleEvidenceClient, IDisposable
{
    private readonly object _sync = new();
    private readonly EvidenceDeliveryState _evidence;
    private readonly ResolvedDebugBundleOptions _options;
    private readonly IEventTransport? _transport;
    private readonly IRemoteConfigFetcher? _remoteConfigFetcher;
    private readonly DebugBundleRedactor _redactor;
    private readonly ProbeBuffer _probes;
    private readonly SuppressionTracker _suppression = new();
    private readonly ExceptionCaptureIdentity _exceptionIdentity = new();
    private readonly Dictionary<string, object?> _persistentContext = new(StringComparer.Ordinal);
    private readonly List<DebugBundleEventEnvelope> _buffer;
    private Timer? _flushTimer;
    private Timer? _remoteConfigTimer;
    private string? _remoteConfigETag;
    private SdkRemoteConfig _remoteConfig = SdkRemoteConfig.Balanced();
    private DateTimeOffset? _retryUntil;
    private int _failures;
    private bool _disposed;
    private Task _initialRemoteConfigTask = Task.CompletedTask;

    private DebugBundleClient(ResolvedDebugBundleOptions options)
    {
        _options = options;
        _evidence = new EvidenceDeliveryState(options.EvidenceCapabilities);
        _redactor = new DebugBundleRedactor(options.RedactFields);
        _probes = new ProbeBuffer(options.MaxProbeLabels, options.MaxProbeEntriesPerLabel);
        _buffer = new List<DebugBundleEventEnvelope>(options.BatchSize);
        try
        {
            _transport = ResolveTransport(options);
        }
        catch
        {
            _transport = null;
        }
        Status = options.Enabled && _transport != null ? DebugBundleStatus.Healthy : DebugBundleStatus.Disconnected;
        _remoteConfigFetcher = ResolveRemoteConfigFetcher(options);
        if (_remoteConfigFetcher != null)
        {
            _remoteConfig = SdkRemoteConfig.Minimal();
            _initialRemoteConfigTask = Task.Run(async () =>
            {
                try { await RefreshRemoteConfigAsync(CancellationToken.None).ConfigureAwait(false); }
                catch { /* Failed configuration fetch retains the restrictive initial policy. */ }
            });
        }
    }

    internal Task InitialRemoteConfigTask => _initialRemoteConfigTask;

    public DebugBundleStatus Status { get; private set; }
    public DateTimeOffset? LastEventAt { get; private set; }

    public static DebugBundleClient Create(DebugBundleOptions options) => new(options.Resolve());

    public void CaptureException(Exception? exception, IDictionary<string, object?>? context = null) => CaptureExceptionCore(exception, context, false);
    internal void CaptureFatal(Exception? exception, IDictionary<string, object?>? context = null) => CaptureExceptionCore(exception, context, true);
    internal void CaptureLoggedException(Exception exception, IDictionary<string, object?> context, Action<DebugBundleEventEnvelope>? projection) =>
        CaptureExceptionCore(exception, context, false, projection);
    private void CaptureExceptionCore(Exception? exception, IDictionary<string, object?>? context, bool fatal, Action<DebugBundleEventEnvelope>? projection = null)
    {
        if (exception == null)
        {
            return;
        }
        if (!MightAdmit(3, "exception")) return;

        try
        {
            var exceptionContext = context == null
                ? new Dictionary<string, object?>()
                : SafeContext(context);
            var operation = OperationContext.Current;
            // Deferred hooks and sampling can reject the first queued report.
            var identityScope = _options.BeforeSend == null && _options.SampleRate >= 1 && operation?.Active(_operations) == true ? operation.Id : null;
            var automatic = fatal || exceptionContext.ContainsKey("logger");
            if (!_exceptionIdentity.Admit(exception, identityScope, automatic)) return;
            var summary = ExceptionProjection.Snapshot(exception);
            var message = summary["message"];
            var payload = new Dictionary<string, object?>
            {
                ["name"] = exception.GetType().FullName ?? exception.GetType().Name,
                ["message"] = message,
                ["handled"] = !fatal,
                ["stack"] = summary["stack"],
                ["request"] = new Dictionary<string, object?>
                {
                    ["method"] = "UNKNOWN",
                    ["path"] = "/",
                    ["query"] = new Dictionary<string, object?>(),
                    ["headers"] = new Dictionary<string, object?>()
                },
                ["response"] = new Dictionary<string, object?>
                {
                    ["status_code"] = 0
                },
                ["runtime"] = BuildRuntimeFacts()
            };

            exceptionContext["exception_hresult"] = exception.HResult;
            if (summary.TryGetValue("inner_exception", out var inner)) exceptionContext["inner_exception"] = inner;

            if (_options.ProbeFlushOnError)
            {
                var probeData = SnapshotProbes();
                if (probeData.Count > 0)
                {
                    payload["probe_data"] = new Dictionary<string, object?>
                    {
                        ["version"] = 1,
                        ["items"] = probeData
                    };
                }
            }

            if (Capture("backend_exception", payload, exceptionContext, exception, projection, fatal))
                _exceptionIdentity.Admit(exception, identityScope, automatic, accepted: true);
        }
        catch { /* Malformed host exceptions cannot escape the public capture boundary. */ }
    }

    public void CaptureError(Exception? exception, IDictionary<string, object?>? context = null) => CaptureException(exception, context);

    public void CaptureLog(string? message, DebugBundleLogLevel level = DebugBundleLogLevel.Information, IDictionary<string, object?>? context = null)
    {
        if (string.IsNullOrWhiteSpace(message) || !_options.Enabled || level < _options.LogLevel || !ShouldCaptureLogByPolicy(level))
        {
            return;
        }
        if (!MightAdmit(level >= DebugBundleLogLevel.Error ? 2 : 0, LevelName(level))) return;

        Capture("log_event", new Dictionary<string, object?>
        {
            ["message"] = message,
            ["level"] = LevelName(level),
            ["attributes"] = context ?? new Dictionary<string, object?>()
        });
    }

    internal bool CanAdmitLog(DebugBundleLogLevel level) =>
        _options.Enabled && level >= _options.LogLevel && ShouldCaptureLogByPolicy(level) &&
        MightAdmit(level >= DebugBundleLogLevel.Error ? 2 : 0, LevelName(level));

    internal void CaptureLogWithProjection(string? message, DebugBundleLogLevel level,
        IDictionary<string, object?> context, Exception? exception, Action<DebugBundleEventEnvelope>? projection)
    {
        if (string.IsNullOrWhiteSpace(message) || !_options.Enabled || level < _options.LogLevel || !ShouldCaptureLogByPolicy(level)) return;
        if (!MightAdmit(level >= DebugBundleLogLevel.Error ? 2 : 0, LevelName(level))) return;
        Capture("log_event", new Dictionary<string, object?>
        {
            ["message"] = message,
            ["level"] = LevelName(level),
            ["attributes"] = context
        }, exception: exception, projection: projection);
    }

    public void CaptureRequest(DebugBundleRequestInfo? request, DebugBundleResponseInfo? response, IDictionary<string, object?>? context = null)
    {
        if (request == null)
        {
            return;
        }

        if (!ShouldCaptureRequestByPolicy(Math.Max(0, response?.StatusCode ?? 0), request.Path, request.Method))
        {
            return;
        }
        if (!MightAdmit((response?.StatusCode ?? 0) >= 400 ? 2 : 1, "request")) return;

        var payload = new Dictionary<string, object?>
        {
            ["method"] = string.IsNullOrWhiteSpace(request.Method) ? "UNKNOWN" : request.Method,
            ["path"] = string.IsNullOrWhiteSpace(request.Path) ? "/" : request.Path,
            ["query"] = request.Query,
            ["headers"] = FilterHeaders(request.Headers),
            ["response_status"] = Math.Max(0, response?.StatusCode ?? 0),
            ["duration_ms"] = response == null ? 0 : Math.Max(0, (long)response.Duration.TotalMilliseconds)
        };

        if (!string.IsNullOrWhiteSpace(request.RouteTemplate))
        {
            payload["route_template"] = request.RouteTemplate;
        }
        if (response != null)
        {
            payload["response_headers"] = FilterHeaders(response.Headers);
        }

        var requestHeaders = FilterHeaders(request.Headers);
        var requestContext = context == null
            ? new Dictionary<string, object?>()
            : SafeContext(context);
        if (requestHeaders.TryGetValue("x-debugbundle-trace-id", out var traceId) && !string.IsNullOrWhiteSpace(traceId))
        {
            requestContext["trace_id"] = traceId;
        }

        if (requestHeaders.TryGetValue("x-request-id", out var requestId) && !string.IsNullOrWhiteSpace(requestId))
        {
            requestContext["request_id"] = requestId;
        }
        else if (request.Headers.TryGetValue("X-Correlation-ID", out var correlationId) && !string.IsNullOrWhiteSpace(correlationId))
        {
            requestContext["request_id"] = correlationId;
        }

        Capture("request_event", payload, requestContext);
    }

    public void CaptureMessage(string? message, DebugBundleLogLevel level = DebugBundleLogLevel.Information, IDictionary<string, object?>? context = null)
    {
        if (string.IsNullOrWhiteSpace(message) || !_options.Enabled || level < _options.LogLevel || !ShouldCaptureLogByPolicy(level))
        {
            return;
        }
        if (!MightAdmit(level >= DebugBundleLogLevel.Error ? 2 : 0, LevelName(level))) return;

        Capture("log_event", new Dictionary<string, object?>
        {
            ["message"] = message,
            ["level"] = LevelName(level),
            ["attributes"] = context ?? new Dictionary<string, object?>()
        });
    }

    public void SetContext(string key, object? value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        object? safe = null;
        if (value != null)
        {
            try
            {
                var protectedContext = SafeContext(new Dictionary<string, object?> { [key] = value }, _options.RedactFields);
                if (!protectedContext.TryGetValue(key, out safe)) return;
            }
            catch { return; }
        }

        lock (_sync)
        {
            if (value == null)
            {
                _persistentContext.Remove(key);
            }
            else _persistentContext[key] = safe;
        }
    }

    public DebugBundleScope BeginScope(IDictionary<string, object?> values)
    {
        var safe = SafeContext(values);
        // This signed SDK control token is consumed locally, never emitted as telemetry.
        if (TelemetryPrivacy.IsSafeContainer(values))
        {
            foreach (var item in values.Take(256))
                if (item.Key == "probe_trigger_token" && item.Value is string text && text.Length <= 16 * 1024)
                    safe["probe_trigger_token"] = text;
        }
        return DebugBundleContext.BeginScope(safe);
    }

    public void SetUserHash(string userHash) => SetContext("user_id_hash", userHash);
    public void SetTraceId(string traceId) => SetContext("trace_id", traceId);
    public void SetRequestId(string requestId) => SetContext("request_id", requestId);

    public void Probe(string label, object? data, ProbeOptions? options = null)
    {
        var activations = MatchingProbeDirectives(label, DateTimeOffset.UtcNow);
        if (options?.Heavy == true && activations.Count == 0)
        {
            return;
        }

        RecordProbe(label, data, activations);
    }

    public void Probe(string label, Func<object?> data, ProbeOptions? options = null)
    {
        if (data == null)
        {
            return;
        }

        var activations = MatchingProbeDirectives(label, DateTimeOffset.UtcNow);
        if (options?.Heavy == true && activations.Count == 0)
        {
            return;
        }

        object? value;
        try
        {
            value = data();
        }
        catch (Exception exception)
        {
            value = new Dictionary<string, object?> { ["probe_error"] = ExceptionProjection.Message(exception) };
        }

        RecordProbe(label, value, activations);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _operations.Closed, 1);
        lock (_sync)
        {
            _disposed = true;
            _diagnostics.Clear();
            _flushTimer?.Dispose();
            _flushTimer = null;
            _remoteConfigTimer?.Dispose();
            _remoteConfigTimer = null;
        }

        if (_transport is IDisposable disposable)
        {
            disposable.Dispose();
        }

        if (_remoteConfigFetcher is IDisposable remoteConfigDisposable)
        {
            remoteConfigDisposable.Dispose();
        }
    }

    private bool Capture(string eventType, Dictionary<string, object?> payload, IDictionary<string, object?>? context = null, Exception? exception = null, Action<DebugBundleEventEnvelope>? projection = null, bool fatal = false)
    {
        try
        {
            if (!_options.Enabled || _transport == null)
            {
                return false;
            }
            if (!MightAdmit(eventType == "backend_exception" ? 3 : eventType == "log_event" ?
                EventPriorityFromPayload(payload) : eventType == "request_event" ?
                RequestPriorityFromPayload(payload) : 1, eventType)) return false;

            var redacted = ToDictionary(_redactor.Redact(payload));
            var envelope = ProtectCandidate(BuildEnvelope(eventType, redacted, context));
            if (envelope == null || !ShouldCapturePreparedEvent(envelope)) return false;
            var eventBytes = EventBytes(envelope);

            lock (_sync)
            {
                if (_disposed)
                {
                    return false;
                }

                envelope.Fatal = fatal;
                envelope.PendingSampling = !fatal;
                envelope.PendingProjection = projection;
                if (exception != null) envelope.PendingException = new WeakReference<Exception>(exception);
                if (envelope.EventType == "diagnostic_context")
                {
                    if (!_diagnostics.Reserve(envelope.EventId, envelope.Payload, eventBytes, DateTimeOffset.UtcNow)) return false;
                    envelope.DiagnosticId = envelope.EventId;
                }
                if (!AdmitLocked(envelope, eventBytes)) { ReleaseDiagnosticLocked(envelope); return false; }
                if (_buffer.Count >= Math.Min(_options.BatchSize, MaxQueuedEvents))
                {
                    RequestFlushLocked();
                }
                else
                {
                    ScheduleFlushLocked();
                }
            }
            return true;
        }
        catch
        {
            Status = DebugBundleStatus.Degraded;
            return false;
        }
    }

    private DebugBundleEventEnvelope BuildEnvelope(string eventType, Dictionary<string, object?> payload, IDictionary<string, object?>? context)
    {
        if (eventType == "backend_exception" && CurrentExecution() is { } execution) payload["execution"] = execution;
        var mergedContext = BuildContext(context);
        if (eventType is "backend_exception" or "frontend_exception") AddFailureReference(mergedContext);
        var correlation = BuildCorrelation(mergedContext, payload);
        var envelopeContext = BuildEnvelopeContext(mergedContext);

        return new DebugBundleEventEnvelope
        {
            EventType = eventType,
            ProjectToken = string.IsNullOrEmpty(_options.ProjectToken) ? null : _options.ProjectToken,
            Service = new DebugBundleServiceDescriptor
            {
                Name = _options.Service,
                Runtime = ".net",
                Environment = _options.Environment
            },
            OccurredAt = DateTimeOffset.UtcNow.UtcDateTime.ToString("O"),
            Correlation = correlation.Count == 0 ? null : correlation,
            Context = envelopeContext.Count == 0 ? null : envelopeContext,
            Payload = payload
        };
    }

    private static Dictionary<string, object?> SafeContext(IDictionary<string, object?> values, IEnumerable<string>? additionalFields = null)
    {
        try
        {
            var safe = TelemetryPrivacy.Protect(values, additionalFields) as Dictionary<string, object?> ?? new Dictionary<string, object?>();
            if (TelemetryPrivacy.IsSafeContainer(values)) RestoreCorrelation(values, safe);
            return safe;
        }
        catch { return new Dictionary<string, object?>(); }
    }

    // Canonical correlation is protected as identity text, not as arbitrary
    // application attributes. In particular the valid session ID "0" must survive.
    private static void RestoreCorrelation(IDictionary<string, object?> source, Dictionary<string, object?> destination)
    {
        foreach (var field in new[] { "trace_id", "request_id", "session_id", "user_id_hash" })
        {
            if (!source.TryGetValue(field, out var value)) continue;
            if (value == null || value is string text && Equals(TelemetryPrivacy.Protect(text), text)) destination[field] = value;
            else destination.Remove(field);
        }
    }

    private Dictionary<string, object?> BuildContext(IDictionary<string, object?>? context)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        lock (_sync)
        {
            foreach (var item in _persistentContext)
            {
                result[item.Key] = item.Value;
            }
        }

        if (DebugBundleContext.Current != null)
        {
            foreach (var item in DebugBundleContext.Current)
            {
                result[item.Key] = item.Value;
            }
        }

        if (context != null)
        {
            foreach (var item in SafeContext(context))
            {
                result[item.Key] = item.Value;
            }
        }

        if (Activity.Current != null)
        {
            AddIfMissing(result, "activity_trace_id", Activity.Current.TraceId.ToString());
            AddIfMissing(result, "activity_span_id", Activity.Current.SpanId.ToString());
        }

        var safe = ToDictionary(_redactor.Redact(result));
        RestoreCorrelation(result, safe);
        return safe;
    }

    private static Dictionary<string, object?> BuildEnvelopeContext(IReadOnlyDictionary<string, object?> context)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var item in context)
        {
            if (item.Key is "trace_id" or "request_id" or "session_id" or "user_id_hash")
            {
                continue;
            }
            result[item.Key] = item.Value;
        }
        return result;
    }

    private static Dictionary<string, object?> BuildCorrelation(IReadOnlyDictionary<string, object?> context, IReadOnlyDictionary<string, object?> payload)
    {
        var correlation = new Dictionary<string, object?>(StringComparer.Ordinal);
        Copy("trace_id");
        Copy("request_id");
        Copy("session_id");
        Copy("user_id_hash");
        if (!correlation.ContainsKey("trace_id") && context.TryGetValue("activity_trace_id", out var activityTrace)) correlation["trace_id"] = activityTrace;
        return correlation;

        void Copy(string key)
        {
            if (context.TryGetValue(key, out var value) && value != null)
            {
                correlation[key] = value;
                return;
            }

            if (payload.TryGetValue(key, out var payloadValue) && payloadValue != null)
            {
                correlation[key] = payloadValue;
            }
        }
    }

}
