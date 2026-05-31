using System.Text.Json.Serialization;

namespace DebugBundle;

public enum DebugBundleProjectMode
{
    Connected,
    LocalOnly
}

public enum DebugBundleStatus
{
    Healthy,
    Degraded,
    Disconnected
}

public enum DebugBundleLogLevel
{
    Trace = 0,
    Debug = 1,
    Information = 2,
    Warning = 3,
    Error = 4,
    Critical = 5
}

public sealed class ProbeOptions
{
    public bool Heavy { get; set; }
}

public sealed class DebugBundleRequestInfo
{
    public string Method { get; set; } = "UNKNOWN";
    public string Path { get; set; } = "/";
    public string? RouteTemplate { get; set; }
    public IDictionary<string, string> Headers { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IDictionary<string, string?> Query { get; set; } = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
}

public sealed class DebugBundleResponseInfo
{
    public int StatusCode { get; set; }
    public TimeSpan Duration { get; set; }
    public IDictionary<string, string> Headers { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed class DebugBundleEventEnvelope
{
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = "1";

    [JsonPropertyName("event_id")]
    public string EventId { get; set; } = Guid.NewGuid().ToString("D");

    [JsonPropertyName("event_type")]
    public string EventType { get; set; } = string.Empty;

    [JsonPropertyName("project_token")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProjectToken { get; set; }

    [JsonPropertyName("sdk_name")]
    public string SdkName { get; set; } = DebugBundleConstants.SdkName;

    [JsonPropertyName("sdk_version")]
    public string SdkVersion { get; set; } = DebugBundleConstants.SdkVersion;

    [JsonPropertyName("service")]
    public DebugBundleServiceDescriptor Service { get; set; } = new();

    [JsonPropertyName("occurred_at")]
    public string OccurredAt { get; set; } = DateTimeOffset.UtcNow.ToString("O");

    [JsonPropertyName("correlation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object?>? Correlation { get; set; }

    [JsonPropertyName("payload")]
    public Dictionary<string, object?> Payload { get; set; } = new();
}

public sealed class DebugBundleServiceDescriptor
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "dotnet-service";

    [JsonPropertyName("runtime")]
    public string Runtime { get; set; } = ".net";

    [JsonPropertyName("framework")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Framework { get; set; }

    [JsonPropertyName("environment")]
    public string Environment { get; set; } = "development";
}

public static class DebugBundleConstants
{
    public const string SdkName = "@debugbundle/sdk-dotnet";
    public const string SdkVersion = "1.0.0";
}
