using DebugBundle.Hangfire;
using Hangfire;

namespace DebugBundle.Hangfire.Tests;

public sealed class HangfireGlobalConfigurationExtensionTests
{
    [Fact]
    public void UseDebugBundle_Registers_Default_And_Explicit_Client_Filters()
    {
        var originalFilters = GlobalJobFilters.Filters.ToArray();
        var originalDebugBundleFilters = originalFilters.Count(
            filter => filter.Instance is DebugBundleHangfireFilter);
        try
        {
            var configuration = GlobalConfiguration.Configuration;

            Assert.Same(configuration, configuration.UseDebugBundle(new FakeClient()));
            Assert.Same(configuration, configuration.UseDebugBundle());

            Assert.Equal(
                originalDebugBundleFilters + 2,
                GlobalJobFilters.Filters.Count(filter => filter.Instance is DebugBundleHangfireFilter));
        }
        finally
        {
            foreach (var filter in GlobalJobFilters.Filters.Except(originalFilters).ToArray())
            {
                GlobalJobFilters.Filters.Remove(filter.Instance);
            }
        }
    }

    [Fact]
    public void UseDebugBundle_Rejects_Null_Configuration()
    {
        Assert.Throws<ArgumentNullException>(() =>
            HangfireGlobalConfigurationExtensions.UseDebugBundle(null!, new FakeClient()));
        Assert.Throws<ArgumentNullException>(() =>
            HangfireGlobalConfigurationExtensions.UseDebugBundle(null!));
    }
}
