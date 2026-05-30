using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace DebugBundle.Logging;

public static class LoggingBuilderExtensions
{
    public static ILoggingBuilder AddDebugBundle(this ILoggingBuilder builder)
    {
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, DebugBundleLoggerProvider>());
        return builder;
    }

    public static ILoggingBuilder AddDebugBundle(this ILoggingBuilder builder, Action<DebugBundleLoggerOptions> configure)
    {
        builder.Services.Configure(configure);
        return builder.AddDebugBundle();
    }

    public static ILoggingBuilder AddDebugBundle(this ILoggingBuilder builder, IDebugBundleClient client)
    {
        builder.Services.TryAddSingleton(client);
        return builder.AddDebugBundle();
    }
}
