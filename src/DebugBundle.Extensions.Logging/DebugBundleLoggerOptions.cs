using Microsoft.Extensions.Logging;

namespace DebugBundle.Logging;

public sealed class DebugBundleLoggerOptions
{
    public LogLevel MinimumLevel { get; set; } = LogLevel.Warning;
    public bool IncludeScopes { get; set; } = true;
    public bool IncludeExceptionDetails { get; set; } = true;
    public int MaxStructuredFields { get; set; } = 50;
    public ISet<string> ExcludedCategoryPrefixes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "DebugBundle"
    };
}
