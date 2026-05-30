using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Hosting;

namespace DebugBundle.AzureFunctions;

public static class FunctionsWorkerApplicationBuilderExtensions
{
    public static IFunctionsWorkerApplicationBuilder UseDebugBundle(this IFunctionsWorkerApplicationBuilder builder)
    {
        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        builder.UseMiddleware<DebugBundleFunctionsMiddleware>();
        return builder;
    }
}
