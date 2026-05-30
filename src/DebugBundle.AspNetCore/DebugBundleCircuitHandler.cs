using Microsoft.AspNetCore.Components.Server.Circuits;

namespace DebugBundle.AspNetCore;

public sealed class DebugBundleCircuitHandler : CircuitHandler
{
    private readonly IDebugBundleClient _client;

    public DebugBundleCircuitHandler(IDebugBundleClient client)
    {
        _client = client;
    }

    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(Func<CircuitInboundActivityContext, Task> next)
    {
        return async context =>
        {
            try
            {
                await next(context).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                try
                {
                    _client.CaptureException(exception, BuildContext(context));
                }
                catch
                {
                    // Blazor circuit capture is observational and must not affect circuit behavior.
                }

                throw;
            }
        };
    }

    private static Dictionary<string, object?> BuildContext(CircuitInboundActivityContext? context)
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["framework"] = "blazor-server",
            ["circuit_id"] = TryCircuitId(context)
        };
    }

    private static string? TryCircuitId(CircuitInboundActivityContext? context)
    {
        try
        {
            return context?.Circuit?.Id;
        }
        catch
        {
            return null;
        }
    }
}
