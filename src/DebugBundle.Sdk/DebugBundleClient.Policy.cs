namespace DebugBundle;

public sealed partial class DebugBundleClient
{
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
