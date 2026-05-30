using Hangfire;

namespace DebugBundle.Hangfire;

public static class HangfireGlobalConfigurationExtensions
{
    public static IGlobalConfiguration UseDebugBundle(this IGlobalConfiguration configuration, IDebugBundleClient client)
    {
        if (configuration == null)
        {
            throw new ArgumentNullException(nameof(configuration));
        }

        GlobalJobFilters.Filters.Add(new DebugBundleHangfireFilter(client));
        return configuration;
    }

    public static IGlobalConfiguration UseDebugBundle(this IGlobalConfiguration configuration)
    {
        if (configuration == null)
        {
            throw new ArgumentNullException(nameof(configuration));
        }

        GlobalJobFilters.Filters.Add(new DebugBundleHangfireFilter());
        return configuration;
    }
}
