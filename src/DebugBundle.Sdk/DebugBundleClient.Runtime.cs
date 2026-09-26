using System.Diagnostics;
using System.Runtime.InteropServices;
using DebugBundle.Redaction;
using DebugBundle.Transport;

namespace DebugBundle;

public sealed partial class DebugBundleClient
{
    private void RecordProbe(string label, object? data, IReadOnlyList<ProbeDirective> activations)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return;
        }

        Dictionary<string, object?> redacted;
        try
        {
            label = (string)TelemetryPrivacy.Protect(label, _options.RedactFields)!;
            redacted = NormalizeProbeData(TelemetryPrivacy.Protect(_redactor.Redact(data), _options.RedactFields));
        }
        catch { return; }
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

        if (!config.RemoteProbesEnabled)
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

    private static Dictionary<string, object?> NormalizeProbeData(object? redacted)
    {
        if (redacted is Dictionary<string, object?> dictionary)
        {
            return dictionary;
        }

        return new Dictionary<string, object?> { ["value"] = redacted };
    }

    private bool ShouldCapturePreparedEvent(DebugBundleEventEnvelope envelope)
    {
        if (envelope.EventType == "log_event")
        {
            var level = ParseLogLevel(envelope.Payload.TryGetValue("level", out var value) ? value : null);
            return level >= _options.LogLevel && ShouldCaptureLogByPolicy(level);
        }

        if (envelope.EventType == "request_event")
        {
            return ShouldCaptureRequestByPolicy(
                Convert.ToInt32(envelope.Payload["response_status"], System.Globalization.CultureInfo.InvariantCulture),
                Convert.ToString(envelope.Payload["path"], System.Globalization.CultureInfo.InvariantCulture),
                Convert.ToString(envelope.Payload["method"], System.Globalization.CultureInfo.InvariantCulture));
        }

        if (envelope.EventType == "probe_event")
        {
            return ShouldEmitProbeEventsByPolicy();
        }

        return true;
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

    private void ScheduleRetryLocked()
    {
        if (_retryUntil == null || _disposed) return;
        var remaining = _retryUntil.Value - DateTimeOffset.UtcNow;
        if (remaining < TimeSpan.FromMilliseconds(1)) remaining = TimeSpan.FromMilliseconds(1);
        _flushTimer?.Dispose();
        _flushTimer = new Timer(_ => _ = FlushAsync(), null, remaining, Timeout.InfiniteTimeSpan);
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

    private static TimeSpan BoundedRetryAfter(TimeSpan? hint, int failures)
    {
        if (hint == null || hint < TimeSpan.Zero) return DefaultBackoff(failures);
        return hint > TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(5) : hint.Value;
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
        if (!TelemetryPrivacy.IsSafeContainer(headers)) return new Dictionary<string, string>();
        return headers
            .Take(256)
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
            ["framework_extras"] = new Dictionary<string, object?>
            {
                ["gc_server"] = System.Runtime.GCSettings.IsServerGC
            }
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

    private static DebugBundleLogLevel ParseLogLevel(object? value)
    {
        return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)?.ToLowerInvariant() switch
        {
            "trace" => DebugBundleLogLevel.Trace,
            "debug" => DebugBundleLogLevel.Debug,
            "warning" => DebugBundleLogLevel.Warning,
            "error" => DebugBundleLogLevel.Error,
            "critical" => DebugBundleLogLevel.Critical,
            _ => DebugBundleLogLevel.Information
        };
    }
}
