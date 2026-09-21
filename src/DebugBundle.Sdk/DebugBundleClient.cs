using System.Diagnostics;
using DebugBundle.Redaction;
using DebugBundle.Transport;

namespace DebugBundle;

public sealed partial class DebugBundleClient : IDebugBundleClient, IDisposable
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
            ["name"] = exception.GetType().FullName ?? exception.GetType().Name,
            ["message"] = string.IsNullOrWhiteSpace(exception.Message)
                ? exception.GetType().Name
                : exception.Message,
            ["handled"] = true,
            ["stack"] = exception.ToString(),
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

        var exceptionContext = context == null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(context, StringComparer.Ordinal);
        exceptionContext["exception_hresult"] = exception.HResult;
        if (exception.InnerException != null)
        {
            exceptionContext["inner_exception"] = new Dictionary<string, object?>
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

        Capture("backend_exception", payload, exceptionContext);
    }

    public void CaptureError(Exception? exception, IDictionary<string, object?>? context = null) => CaptureException(exception, context);

    public void CaptureLog(string? message, DebugBundleLogLevel level = DebugBundleLogLevel.Information, IDictionary<string, object?>? context = null)
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

    public void CaptureRequest(DebugBundleRequestInfo? request, DebugBundleResponseInfo? response, IDictionary<string, object?>? context = null)
    {
        if (request == null)
        {
            return;
        }

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

        var requestContext = context == null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(context, StringComparer.Ordinal);
        if (request.Headers.TryGetValue("X-DebugBundle-Trace-Id", out var traceId) && !string.IsNullOrWhiteSpace(traceId))
        {
            requestContext["trace_id"] = traceId;
        }

        if (request.Headers.TryGetValue("X-Request-ID", out var requestId) && !string.IsNullOrWhiteSpace(requestId))
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
                try
                {
                    var protectedContext = (Dictionary<string, object?>)TelemetryPrivacy.Protect(
                        new Dictionary<string, object?> { [key] = value }, _options.RedactFields)!;
                    if (protectedContext.TryGetValue(key, out var safe)) _persistentContext[key] = safe;
                }
                catch { /* Unsupported values must not enter SDK-owned context. */ }
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
                var aggregateEnvelope = BuildEnvelope("error_suppressed", new Dictionary<string, object?>
                {
                    ["fingerprint"] = aggregate.Fingerprint,
                    ["suppressed_count"] = aggregate.SuppressedCount,
                    ["first_seen"] = aggregate.FirstSeen.ToString("O"),
                    ["last_seen"] = aggregate.LastSeen.ToString("O"),
                    ["window_seconds"] = aggregate.WindowSeconds
                }, null);
                var safeAggregate = TelemetryPrivacy.ProtectEvent(aggregateEnvelope, _options.RedactFields);
                var preparedAggregate = safeAggregate == null ? null : BeforeSendProcessor.Apply(safeAggregate, _options.BeforeSend);
                preparedAggregate = preparedAggregate == null ? null : TelemetryPrivacy.ProtectEvent(preparedAggregate, _options.RedactFields);
                if (preparedAggregate != null && BeforeSendProcessor.IsValid(preparedAggregate))
                {
                    batch.Add(preparedAggregate);
                }
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

                var acknowledgement = IngestionAcknowledgement.Decide(response.Body, batch.Count);
                if (acknowledgement.Kind == IngestionAcknowledgementKind.ProtocolFailure)
                {
                    _buffer.InsertRange(0, batch);
                    _failures++;
                    Status = DebugBundleStatus.Degraded;
                    _retryUntil = DateTimeOffset.UtcNow + (response.RetryAfter ?? DefaultBackoff(_failures));
                    return;
                }

                if (acknowledgement.Kind == IngestionAcknowledgementKind.Acknowledged)
                {
                    var retryableEvents = batch
                        .Where((_, index) => acknowledgement.RetryableIndices.Contains(index))
                        .ToList();
                    if (retryableEvents.Count > 0)
                    {
                        _buffer.InsertRange(0, retryableEvents);
                    }

                    if (acknowledgement.Accepted > 0)
                    {
                        LastEventAt = DateTimeOffset.UtcNow;
                    }

                    if (retryableEvents.Count > 0)
                    {
                        _failures++;
                        Status = DebugBundleStatus.Degraded;
                        _retryUntil = DateTimeOffset.UtcNow + (response.RetryAfter ?? DefaultBackoff(_failures));
                        return;
                    }

                    _failures = 0;
                    Status = acknowledgement.Accepted > 0
                        ? DebugBundleStatus.Healthy
                        : DebugBundleStatus.Disconnected;
                    _retryUntil = null;
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
            if (!_options.Enabled || _transport == null)
            {
                return;
            }

            var redacted = ToDictionary(_redactor.Redact(payload));
            var initial = TelemetryPrivacy.ProtectEvent(BuildEnvelope(eventType, redacted, context), _options.RedactFields);
            if (initial == null) return;
            var prepared = BeforeSendProcessor.Apply(initial, _options.BeforeSend);
            var envelope = prepared == null ? null : TelemetryPrivacy.ProtectEvent(prepared, _options.RedactFields);
            if (envelope == null ||
                !ShouldCapturePreparedEvent(envelope) ||
                _options.RandomSource() > _options.SampleRate)
            {
                return;
            }

            var fingerprint = SuppressionTracker.Fingerprint(envelope.EventType, envelope.Payload);
            if (envelope.EventType != "probe_event" &&
                !_suppression.ShouldCapture(fingerprint, DateTimeOffset.UtcNow))
            {
                return;
            }

            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                var buffered = TelemetryPrivacy.ProtectEvent(envelope, _options.RedactFields);
                if (buffered == null) return;
                _buffer.Add(buffered);
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
        var envelopeContext = BuildEnvelopeContext(mergedContext);

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
            Context = envelopeContext.Count == 0 ? null : envelopeContext,
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

    private bool ShouldCaptureRequestByPolicy(int statusCode, string? requestPath, string? httpMethod)
    {
        var mode = _remoteConfig.CapturePolicy.CaptureRequestEvents;
        if (mode == "all")
        {
            return true;
        }

        if (IsImmediateRequestFailure(statusCode, requestPath, httpMethod))
        {
            return true;
        }

        if (mode == "off" || mode == "filtered")
        {
            return false;
        }

        if (mode == "failures_only")
        {
            return statusCode >= 500;
        }

        return false;
    }

    private bool IsImmediateRequestFailure(int statusCode, string? requestPath = null, string? httpMethod = null)
    {
        if (statusCode >= 500)
        {
            return true;
        }

        if (_remoteConfig.CapturePolicy.ImmediateClientErrorStatuses.Contains(statusCode))
        {
            return true;
        }

        if (MatchesImmediateClientErrorPathRule(statusCode, requestPath, httpMethod))
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

    private bool MatchesImmediateClientErrorPathRule(int statusCode, string? requestPath, string? httpMethod)
    {
        if (statusCode < 400 || statusCode > 499 || string.IsNullOrWhiteSpace(requestPath))
        {
            return false;
        }

        var normalizedPath = NormalizeRequestPath(requestPath!);
        var normalizedMethod = string.IsNullOrWhiteSpace(httpMethod) ? null : httpMethod!.Trim().ToUpperInvariant();
        foreach (var rule in _remoteConfig.CapturePolicy.ImmediateClientErrorPathRules)
        {
            if (rule.StatusCode != statusCode || string.IsNullOrWhiteSpace(rule.PathPattern))
            {
                continue;
            }
            if (rule.Methods.Count > 0)
            {
                if (rule.Methods.Count > 7)
                {
                    continue;
                }
                var ruleMethods = rule.Methods
                    .Select(NormalizeCapturePolicyMethod)
                    .Where(method => method != null)
                    .ToList();
                if (ruleMethods.Count == 0 || normalizedMethod == null || !ruleMethods.Contains(normalizedMethod))
                {
                    continue;
                }
            }
            var pathPattern = rule.PathPattern ?? string.Empty;
            if (pathPattern.EndsWith("*", StringComparison.Ordinal))
            {
                if (normalizedPath.StartsWith(pathPattern.Substring(0, pathPattern.Length - 1), StringComparison.Ordinal))
                {
                    return true;
                }
                continue;
            }
            if (string.Equals(normalizedPath, pathPattern, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string? NormalizeCapturePolicyMethod(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value!.Trim().ToUpperInvariant();
        return normalized is "GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD" or "OPTIONS" ? normalized : null;
    }

    private static string NormalizeRequestPath(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute) && !string.IsNullOrWhiteSpace(absolute.AbsolutePath))
        {
            return absolute.AbsolutePath;
        }

        var queryIndex = value.IndexOf('?');
        var fragmentIndex = value.IndexOf('#');
        var end = queryIndex < 0
            ? (fragmentIndex < 0 ? value.Length : fragmentIndex)
            : (fragmentIndex < 0 ? queryIndex : Math.Min(queryIndex, fragmentIndex));
        var path = value.Substring(0, end);
        return path.StartsWith("/", StringComparison.Ordinal) && path.Length > 0 ? path : "/";
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

}
