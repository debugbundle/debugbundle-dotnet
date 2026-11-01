using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DebugBundle;

public interface IRemoteConfigFetcher
{
    Task<RemoteConfigFetchResult> FetchAsync(RemoteConfigFetchRequest request, CancellationToken cancellationToken = default);
}

public sealed class RemoteConfigFetchRequest
{
    public Uri Endpoint { get; set; } = new("https://api.debugbundle.com/v1/events");
    public string ProjectToken { get; set; } = string.Empty;
    public string Service { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;
    public string? ETag { get; set; }
}

public sealed class RemoteConfigFetchResult
{
    public bool NotModified { get; set; }
    public string? ETag { get; set; }
    public SdkRemoteConfig Config { get; set; } = SdkRemoteConfig.Minimal();
}

public sealed class SdkRemoteConfig
{
    [JsonPropertyName("evidence_capabilities")]
    public JsonElement? EvidenceCapabilities { get; set; }
    [JsonPropertyName("probes_enabled")]
    public bool ProbesEnabled { get; set; } = true;

    [JsonPropertyName("remote_probes_enabled")]
    public bool RemoteProbesEnabled { get; set; }

    [JsonPropertyName("active_probes")]
    public List<ProbeDirective> ActiveProbes { get; set; } = new();

    [JsonPropertyName("poll_interval_ms")]
    public int? PollIntervalMs { get; set; }

    [JsonPropertyName("trigger_token_key")]
    public string? TriggerTokenKey { get; set; }

    [JsonPropertyName("capture_policy")]
    public CapturePolicy CapturePolicy { get; set; } = CapturePolicy.Balanced();

    public static SdkRemoteConfig Minimal()
    {
        return new SdkRemoteConfig
        {
            ProbesEnabled = true,
            RemoteProbesEnabled = false,
            CapturePolicy = CapturePolicy.Minimal()
        };
    }

    public static SdkRemoteConfig Balanced()
    {
        return new SdkRemoteConfig
        {
            ProbesEnabled = true,
            RemoteProbesEnabled = false,
            CapturePolicy = CapturePolicy.Balanced()
        };
    }
}

public sealed class ProbeDirective
{
    [JsonPropertyName("activation_id")]
    public string? ActivationId { get; set; }

    [JsonPropertyName("label_pattern")]
    public string LabelPattern { get; set; } = string.Empty;

    [JsonPropertyName("service")]
    public string? Service { get; set; }

    [JsonPropertyName("environment")]
    public string? Environment { get; set; }

    [JsonPropertyName("expires_at")]
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class CapturePolicy
{
    [JsonPropertyName("capture_diagnostic_context")]
    public bool CaptureDiagnosticContext { get; set; } = true;
    [JsonPropertyName("preset")]
    public string Preset { get; set; } = "balanced";

    [JsonPropertyName("capture_logs")]
    public string CaptureLogs { get; set; } = "warning";

    [JsonPropertyName("capture_request_events")]
    public string CaptureRequestEvents { get; set; } = "failures_only";

    [JsonPropertyName("capture_probe_events")]
    public string CaptureProbeEvents { get; set; } = "buffer_only";

    [JsonPropertyName("immediate_client_error_statuses")]
    public List<int> ImmediateClientErrorStatuses { get; set; } = new();

    [JsonPropertyName("immediate_client_error_path_rules")]
    public List<ImmediateClientErrorPathRule> ImmediateClientErrorPathRules { get; set; } = new();

    public static CapturePolicy Minimal()
    {
        return new CapturePolicy
        {
            Preset = "minimal",
            CaptureDiagnosticContext = false,
            CaptureLogs = "warning",
            CaptureRequestEvents = "failures_only",
            CaptureProbeEvents = "buffer_only"
        };
    }

    public static CapturePolicy Balanced()
    {
        return new CapturePolicy();
    }
}

public sealed class ImmediateClientErrorPathRule
{
    [JsonPropertyName("status_code")]
    public int StatusCode { get; set; }

    [JsonPropertyName("path_pattern")]
    public string PathPattern { get; set; } = "/";

    [JsonPropertyName("methods")]
    public List<string> Methods { get; set; } = new();
}

public sealed class HttpRemoteConfigFetcher : IRemoteConfigFetcher, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _timeout;
    private readonly bool _ownsClient;

    public HttpRemoteConfigFetcher(TimeSpan timeout, HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _ownsClient = httpClient == null;
        _timeout = timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(5) : timeout;
        _httpClient.Timeout = _timeout;
    }

    public async Task<RemoteConfigFetchResult> FetchAsync(RemoteConfigFetchRequest request, CancellationToken cancellationToken = default)
    {
        var configUri = BuildConfigUri(request.Endpoint, request.Service, request.Environment);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, configUri);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ProjectToken);
        if (!string.IsNullOrWhiteSpace(request.ETag))
        {
            httpRequest.Headers.TryAddWithoutValidation("If-None-Match", request.ETag);
        }

        httpRequest.Headers.Add("X-DebugBundle-Evidence", "1");
        httpRequest.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
        {
            return new RemoteConfigFetchResult { NotModified = true, ETag = request.ETag };
        }

        response.EnsureSuccessStatusCode();
        var body = await BoundedHttpResponse.ReadAsync(response, deadline.Token).ConfigureAwait(false);
        var config = JsonSerializer.Deserialize<SdkRemoteConfig>(body, Transport.JsonSerialization.Options) ?? SdkRemoteConfig.Balanced();
        if (config.CapturePolicy == null)
        {
            config.CapturePolicy = CapturePolicy.Balanced();
        }

        return new RemoteConfigFetchResult
        {
            ETag = response.Headers.ETag?.Tag,
            Config = config
        };
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }

    private static Uri BuildConfigUri(Uri endpoint, string service, string environment)
    {
        var builder = new UriBuilder(endpoint)
        {
            Path = "/v1/sdk/config",
            Query = $"service={Uri.EscapeDataString(service)}&environment={Uri.EscapeDataString(environment)}"
        };
        return builder.Uri;
    }
}
