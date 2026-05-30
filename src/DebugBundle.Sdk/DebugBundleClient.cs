using System.Diagnostics;
using System.Runtime.InteropServices;
using DebugBundle.Redaction;
using DebugBundle.Transport;

namespace DebugBundle;

public sealed class DebugBundleClient : IDebugBundleClient, IDisposable
{
    private readonly object _sync = new();
    private readonly ResolvedDebugBundleOptions _options;
    private readonly IEventTransport? _transport;
    private readonly IRemoteConfigFetcher? _remoteConfigFetcher;
    private readonly DebugBundleRedactor _redactor;
    private readonly ProbeBuffer _probes;
    private readonly SuppressionTracker _suppression = new();
    private readonly Dictionary<string, object?> _persistentContext = new(StringComparer.Ordinal);
    private readonly List<DebugBundleEventEnvelope> _buffer;
    private Timer? _flushTimer;
    private Timer? _remoteConfigTimer;
    private string? _remoteConfigETag;
    private SdkRemoteConfig _remoteConfig = SdkRemoteConfig.Balanced();
    private DateTimeOffset? _retryUntil;
    private int _failures;
    private bool _disposed;

    private DebugBundleClient(ResolvedDebugBundleOptions options)
    {
        _options = options;
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
            try
            {
                RefreshRemoteConfigAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch
            {
                _remoteConfig = SdkRemoteConfig.Minimal();
            }
        }
    }

    public DebugBundleStatus Status { get; private set; }
    public DateTimeOffset? LastEventAt { get; private set; }

    public static DebugBundleClient Create(DebugBundleOptions options) => new(options.Resolve());

    public void CaptureException(Exception? exception, IDictionary<string, object?>? context = null)
    {
        if (exception == null)
        {
            return;
        }

        var payload = new Dictionary<string, object?>
        {
            ["name"] = exception.GetType().FullName,
            ["message"] = exception.Message,
            ["handled"] = true,
            ["stack"] = exception.ToString(),
            ["hresult"] = exception.HResult,
            ["runtime"] = BuildRuntimeFacts()
        };

        if (exception.InnerException != null)
        {
            payload["inner_exception"] = new Dictionary<string, object?>
            {
                ["name"] = exception.InnerException.GetType().FullName,
                ["message"] = exception.InnerException.Message,
                ["stack"] = exception.InnerException.ToString()
            };
        }

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

        Capture("backend_exception", payload, context);
    }

    public void CaptureError(Exception? exception, IDictionary<string, object?>? context = null) => CaptureException(exception, context);

    public void CaptureLog(string? message, DebugBundleLogLevel level = DebugBundleLogLevel.Information, IDictionary<string, object?>? context = null)
    {
        if (string.IsNullOrWhiteSpace(message) || level < _options.LogLevel || !ShouldCaptureLogByPolicy(level))
        {
            return;
        }

        Capture("log_event", new Dictionary<string, object?>
        {
            ["message"] = message,
            ["level"] = LevelName(level),
            ["attributes"] = context ?? new Dictionary<string, object?>()
        });
    }

    public void CaptureRequest(DebugBundleRequestInfo? request, DebugBundleResponseInfo? response, IDictionary<string, object?>? context = null)
    {
        if (request == null)
        {
            return;
        }

        if (!ShouldCaptureRequestByPolicy(response?.StatusCode ?? 0))
        {
            return;
        }

        var payload = new Dictionary<string, object?>
        {
            ["method"] = request.Method,
            ["path"] = request.Path,
            ["query"] = request.Query,
            ["headers"] = FilterHeaders(request.Headers),
            ["response_status"] = response?.StatusCode ?? 0,
            ["duration_ms"] = response == null ? 0 : (long)response.Duration.TotalMilliseconds
        };

        if (!string.IsNullOrWhiteSpace(request.RouteTemplate))
        {
            payload["route_template"] = request.RouteTemplate;
        }

        if (request.Headers.TryGetValue("X-DebugBundle-Trace-Id", out var traceId) && !string.IsNullOrWhiteSpace(traceId))
        {
            payload["trace_id"] = traceId;
        }

        if (request.Headers.TryGetValue("X-Request-ID", out var requestId) && !string.IsNullOrWhiteSpace(requestId))
        {
            payload["request_id"] = requestId;
        }
        else if (request.Headers.TryGetValue("X-Correlation-ID", out var correlationId) && !string.IsNullOrWhiteSpace(correlationId))
        {
            payload["request_id"] = correlationId;
        }

        Capture("request_event", payload, context);
    }

    public void CaptureMessage(string? message, DebugBundleLogLevel level = DebugBundleLogLevel.Information, IDictionary<string, object?>? context = null)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

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

        lock (_sync)
        {
            if (value == null)
            {
                _persistentContext.Remove(key);
            }
            else
            {
                _persistentContext[key] = value;
            }
        }
    }

    public DebugBundleScope BeginScope(IDictionary<string, object?> values) => DebugBundleContext.BeginScope(values);

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
            value = new Dictionary<string, object?> { ["probe_error"] = exception.Message };
        }

        RecordProbe(label, value, activations);
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        List<DebugBundleEventEnvelope>? batchToRestore = null;
        try
        {
            IEventTransport? transport;
            List<DebugBundleEventEnvelope> batch;
            lock (_sync)
            {
                if (_disposed || _transport == null)
                {
                    return;
                }

                if (_retryUntil != null && DateTimeOffset.UtcNow < _retryUntil.Value)
                {
                    Status = DebugBundleStatus.Degraded;
                    return;
                }

                transport = _transport;
                batch = new List<DebugBundleEventEnvelope>(_buffer);
                batchToRestore = batch;
                _buffer.Clear();
                _flushTimer?.Dispose();
                _flushTimer = null;
            }

            foreach (var aggregate in _suppression.DrainAggregates(DateTimeOffset.UtcNow))
            {
                batch.Add(BuildEnvelope("error_suppressed", new Dictionary<string, object?>
                {
                    ["fingerprint"] = aggregate.Fingerprint,
                    ["suppressed_count"] = aggregate.SuppressedCount,
                    ["first_seen"] = aggregate.FirstSeen.ToString("O"),
                    ["last_seen"] = aggregate.LastSeen.ToString("O"),
                    ["window_seconds"] = aggregate.WindowSeconds,
                    ["loop_mode"] = aggregate.LoopMode
                }, null));
            }

            if (batch.Count == 0)
            {
                return;
            }

            var response = await transport.SendAsync(new EventTransportRequest
            {
                ProjectToken = _options.ProjectToken,
                Events = batch
            }, cancellationToken).ConfigureAwait(false);
            batchToRestore = null;

            lock (_sync)
            {
                if (response.StatusCode == 429 || response.StatusCode >= 500)
                {
                    _buffer.InsertRange(0, batch);
                    _failures++;
                    Status = DebugBundleStatus.Degraded;
                    _retryUntil = DateTimeOffset.UtcNow + (response.RetryAfter ?? DefaultBackoff(_failures));
                    return;
                }

                if (response.StatusCode >= 400)
                {
                    _failures = 0;
                    Status = DebugBundleStatus.Healthy;
                    return;
                }

                _failures = 0;
                Status = DebugBundleStatus.Healthy;
                LastEventAt = DateTimeOffset.UtcNow;
                _retryUntil = null;
            }
        }
        catch
        {
            lock (_sync)
            {
                if (batchToRestore is { Count: > 0 })
                {
                    _buffer.InsertRange(0, batchToRestore);
                }

                _failures++;
                Status = DebugBundleStatus.Disconnected;
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
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

    private void Capture(string eventType, Dictionary<string, object?> payload, IDictionary<string, object?>? context = null)
    {
        try
        {
            if (!_options.Enabled || _transport == null || _options.RandomSource() > _options.SampleRate)
            {
                return;
            }

            var redacted = ToDictionary(_redactor.Redact(payload));
            var fingerprint = SuppressionTracker.Fingerprint(eventType, redacted);
            if (eventType != "probe_event" && !_suppression.ShouldCapture(fingerprint, DateTimeOffset.UtcNow))
            {
                return;
            }

            var envelope = BuildEnvelope(eventType, redacted, context);

            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _buffer.Add(envelope);
                if (_buffer.Count >= _options.BatchSize)
                {
                    _ = FlushAsync();
                }
                else
                {
                    ScheduleFlushLocked();
                }
            }
        }
        catch
        {
            Status = DebugBundleStatus.Degraded;
        }
    }

    private DebugBundleEventEnvelope BuildEnvelope(string eventType, Dictionary<string, object?> payload, IDictionary<string, object?>? context)
    {
        var mergedContext = BuildContext(context);
        var correlation = BuildCorrelation(mergedContext, payload);
        if (mergedContext.Count > 0)
        {
            payload["context"] = mergedContext;
        }

        return new DebugBundleEventEnvelope
        {
            EventType = eventType,
            ProjectToken = _options.ProjectToken,
            Service = new DebugBundleServiceDescriptor
            {
                Name = _options.Service,
                Runtime = ".net",
                Environment = _options.Environment
            },
            OccurredAt = DateTimeOffset.UtcNow.ToString("O"),
            Correlation = correlation.Count == 0 ? null : correlation,
            Payload = payload
        };
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
            foreach (var item in context)
            {
                result[item.Key] = item.Value;
            }
        }

        if (Activity.Current != null)
        {
            AddIfMissing(result, "activity_trace_id", Activity.Current.TraceId.ToString());
            AddIfMissing(result, "activity_span_id", Activity.Current.SpanId.ToString());
        }

        return ToDictionary(_redactor.Redact(result));
    }

    private static Dictionary<string, object?> BuildCorrelation(IReadOnlyDictionary<string, object?> context, IReadOnlyDictionary<string, object?> payload)
    {
        var correlation = new Dictionary<string, object?>(StringComparer.Ordinal);
        Copy("trace_id");
        Copy("request_id");
        Copy("session_id");
        Copy("user_id_hash");
        Copy("activity_trace_id");
        Copy("activity_span_id");
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

    private void RecordProbe(string label, object? data, IReadOnlyList<ProbeDirective> activations)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return;
        }

        var redacted = _redactor.Redact(data);
        SdkRemoteConfig config;
        lock (_sync)
        {
            config = _remoteConfig;
            if (!config.ProbesEnabled)
            {
                return;
            }

            _probes.Record(label, redacted, DateTimeOffset.UtcNow);
        }

        if (!config.RemoteProbesEnabled || !ShouldEmitProbeEventsByPolicy())
        {
            return;
        }

        foreach (var activation in activations)
        {
            Capture("probe_event", new Dictionary<string, object?>
            {
                ["label"] = label,
                ["data"] = redacted,
                ["activation_id"] = activation.ActivationId,
                ["probe_label_pattern"] = activation.LabelPattern
            });
        }
    }

    private IReadOnlyList<Dictionary<string, object?>> SnapshotProbes()
    {
        lock (_sync)
        {
            return _probes.Snapshot();
        }
    }

    private void ScheduleFlushLocked()
    {
        _flushTimer ??= new Timer(_ => _ = FlushAsync(), null, _options.FlushInterval, Timeout.InfiniteTimeSpan);
    }

    private async Task RefreshRemoteConfigAsync(CancellationToken cancellationToken)
    {
        if (_remoteConfigFetcher == null || string.IsNullOrWhiteSpace(_options.ProjectToken))
        {
            return;
        }

        var result = await _remoteConfigFetcher.FetchAsync(new RemoteConfigFetchRequest
        {
            Endpoint = _options.Endpoint,
            ProjectToken = _options.ProjectToken,
            Service = _options.Service,
            Environment = _options.Environment,
            ETag = _remoteConfigETag
        }, cancellationToken).ConfigureAwait(false);

        lock (_sync)
        {
            if (!result.NotModified)
            {
                _remoteConfig = result.Config ?? SdkRemoteConfig.Balanced();
                _remoteConfigETag = result.ETag;
            }

            ScheduleRemoteConfigRefreshLocked();
        }
    }

    private void ScheduleRemoteConfigRefreshLocked()
    {
        _remoteConfigTimer?.Dispose();
        _remoteConfigTimer = null;
        if (_disposed || !_remoteConfig.RemoteProbesEnabled)
        {
            return;
        }

        var interval = _remoteConfig.PollIntervalMs is > 0
            ? TimeSpan.FromMilliseconds(_remoteConfig.PollIntervalMs.Value)
            : _options.ProbesPollInterval;
        if (HasActiveRemoteProbe(DateTimeOffset.UtcNow) && interval > TimeSpan.FromSeconds(15))
        {
            interval = TimeSpan.FromSeconds(15);
        }

        _remoteConfigTimer = new Timer(_ => _ = RefreshRemoteConfigAsync(CancellationToken.None), null, interval, Timeout.InfiniteTimeSpan);
    }

    private static TimeSpan DefaultBackoff(int failures)
    {
        var seconds = Math.Min(300, Math.Pow(2, Math.Min(failures, 8)));
        return TimeSpan.FromSeconds(seconds);
    }

    private static IEventTransport? ResolveTransport(ResolvedDebugBundleOptions options)
    {
        if (!options.Enabled)
        {
            return null;
        }

        if (options.Transport != null)
        {
            return options.Transport;
        }

        if (options.ProjectMode == DebugBundleProjectMode.LocalOnly || options.Environment is "development" or "local")
        {
            return new FileEventTransport(options.LocalEventsDir);
        }

        if (string.IsNullOrWhiteSpace(options.ProjectToken))
        {
            return null;
        }

        return new HttpEventTransport(options.Endpoint, options.RequestTimeout);
    }

    private static IRemoteConfigFetcher? ResolveRemoteConfigFetcher(ResolvedDebugBundleOptions options)
    {
        if (!options.Enabled || options.ProjectMode == DebugBundleProjectMode.LocalOnly || string.IsNullOrWhiteSpace(options.ProjectToken))
        {
            return null;
        }

        return options.RemoteConfigFetcher ?? new HttpRemoteConfigFetcher(options.RequestTimeout);
    }

    private bool ShouldCaptureLogByPolicy(DebugBundleLogLevel level)
    {
        var mode = _remoteConfig.CapturePolicy.CaptureLogs;
        return mode switch
        {
            "off" => false,
            "error" => level >= DebugBundleLogLevel.Error,
            "warning" => level >= DebugBundleLogLevel.Warning,
            "info" => level >= DebugBundleLogLevel.Information,
            _ => level >= DebugBundleLogLevel.Warning
        };
    }

    private bool ShouldCaptureRequestByPolicy(int statusCode)
    {
        var mode = _remoteConfig.CapturePolicy.CaptureRequestEvents;
        if (mode == "all")
        {
            return true;
        }

        if (IsImmediateRequestFailure(statusCode))
        {
            return true;
        }

        if (mode == "off" || mode == "filtered")
        {
            return false;
        }

        if (mode == "failures_only")
        {
            return IsRequestAnomalyCandidate(statusCode);
        }

        return false;
    }

    private bool IsImmediateRequestFailure(int statusCode)
    {
        if (statusCode >= 500)
        {
            return true;
        }

        if (_remoteConfig.CapturePolicy.ImmediateClientErrorStatuses.Contains(statusCode))
        {
            return true;
        }

        var preset = _remoteConfig.CapturePolicy.Preset;
        if (preset is "balanced" or "investigative" && statusCode is 408 or 423 or 424 or 425 or 429)
        {
            return true;
        }

        return preset == "investigative" && statusCode == 409;
    }

    private bool IsRequestAnomalyCandidate(int statusCode)
    {
        if (_remoteConfig.CapturePolicy.Preset is not ("balanced" or "investigative"))
        {
            return false;
        }

        return statusCode is 400 or 401 or 403 or 404 or 409 or 410 or 422;
    }

    private bool ShouldEmitProbeEventsByPolicy()
    {
        return _remoteConfig.CapturePolicy.CaptureProbeEvents == "standalone_when_activated";
    }

    private bool HasActiveRemoteProbe(DateTimeOffset now)
    {
        return _remoteConfig.ActiveProbes.Any(directive => DirectiveActiveAndScoped(directive, now));
    }

    private IReadOnlyList<ProbeDirective> MatchingProbeDirectives(string label, DateTimeOffset now)
    {
        SdkRemoteConfig config;
        string? triggerToken = null;
        if (DebugBundleContext.Current != null && DebugBundleContext.Current.TryGetValue("probe_trigger_token", out var tokenValue))
        {
            triggerToken = tokenValue as string;
        }

        lock (_sync)
        {
            config = _remoteConfig;
        }

        if (!config.ProbesEnabled)
        {
            return Array.Empty<ProbeDirective>();
        }

        var passive = config.RemoteProbesEnabled
            ? config.ActiveProbes.Where(directive => DirectiveMatches(directive, label, now))
            : Enumerable.Empty<ProbeDirective>();
        var triggered = RemoteProbeTokenValidator.Validate(triggerToken, config.TriggerTokenKey, _options.Service, _options.Environment, now)
            .Where(directive => LabelMatches(directive.LabelPattern, label));
        return passive.Concat(triggered).ToArray();
    }

    private bool DirectiveMatches(ProbeDirective directive, string label, DateTimeOffset now)
    {
        return DirectiveActiveAndScoped(directive, now) && LabelMatches(directive.LabelPattern, label);
    }

    private bool DirectiveActiveAndScoped(ProbeDirective directive, DateTimeOffset now)
    {
        return directive.ExpiresAt > now
            && ScopeMatches(directive.Service, _options.Service)
            && ScopeMatches(directive.Environment, _options.Environment);
    }

    private static bool ScopeMatches(string? configured, string actual)
    {
        return string.IsNullOrWhiteSpace(configured) || configured == "*" || configured!.Equals(actual, StringComparison.Ordinal);
    }

    private static bool LabelMatches(string pattern, string label)
    {
        if (pattern == "*")
        {
            return true;
        }

        if (pattern.EndsWith(".*", StringComparison.Ordinal))
        {
            return label.StartsWith(pattern.Substring(0, pattern.Length - 1), StringComparison.Ordinal);
        }

        return pattern.Equals(label, StringComparison.Ordinal);
    }

    private static Dictionary<string, object?> ToDictionary(object? value)
    {
        if (value is Dictionary<string, object?> typed)
        {
            return typed;
        }

        if (value is IDictionary<string, object> objectDictionary)
        {
            return objectDictionary.ToDictionary(item => item.Key, item => (object?)item.Value, StringComparer.Ordinal);
        }

        return new Dictionary<string, object?> { ["value"] = value };
    }

    private static Dictionary<string, string> FilterHeaders(IDictionary<string, string> headers)
    {
        var allowlist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "user-agent",
            "content-type",
            "accept",
            "x-request-id",
            "x-correlation-id",
            "x-debugbundle-trace-id",
            "traceparent"
        };
        return headers
            .Where(item => allowlist.Contains(item.Key))
            .ToDictionary(item => item.Key.ToLowerInvariant(), item => item.Value, StringComparer.Ordinal);
    }

    private static Dictionary<string, object?> BuildRuntimeFacts()
    {
        return new Dictionary<string, object?>
        {
            ["version"] = RuntimeInformation.FrameworkDescription,
            ["platform"] = RuntimeInformation.OSDescription,
            ["arch"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["pid"] = Process.GetCurrentProcess().Id,
            ["gc_server"] = System.Runtime.GCSettings.IsServerGC
        };
    }

    private static void AddIfMissing(IDictionary<string, object?> target, string key, object? value)
    {
        if (!target.ContainsKey(key))
        {
            target[key] = value;
        }
    }

    private static string LevelName(DebugBundleLogLevel level)
    {
        return level switch
        {
            DebugBundleLogLevel.Trace => "trace",
            DebugBundleLogLevel.Debug => "debug",
            DebugBundleLogLevel.Information => "info",
            DebugBundleLogLevel.Warning => "warning",
            DebugBundleLogLevel.Error => "error",
            DebugBundleLogLevel.Critical => "critical",
            _ => "info"
        };
    }
}
