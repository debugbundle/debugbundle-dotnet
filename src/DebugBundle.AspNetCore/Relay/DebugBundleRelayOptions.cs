namespace DebugBundle.AspNetCore.Relay;

public sealed class DebugBundleRelayOptions
{
    public IList<string> AllowedOrigins { get; } = new List<string>();
    public long MaxBodyBytes { get; set; } = 256 * 1024;
    public int RateLimitPerMinute { get; set; } = 60;
    public bool DurableWrite { get; set; } = true;
    public bool TrustForwardedHeaders { get; set; }
    public string? Service { get; set; }
    public string? Environment { get; set; }
}
