using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;

namespace DebugBundle.AzureFunctions;

public sealed class DebugBundleFunctionsMiddleware : IFunctionsWorkerMiddleware
{
    private readonly IDebugBundleClient _client;

    public DebugBundleFunctionsMiddleware(IDebugBundleClient client)
    {
        _client = client;
    }

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        if (context == null || next == null)
        {
            return;
        }

        var startedAt = DateTimeOffset.UtcNow;
        using var scope = _client.BeginScope(BuildScope(context));
        try
        {
            await next(context).ConfigureAwait(false);
            CaptureInvocation(context, startedAt, 200);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var eventContext = BuildContext(context);
            _client.CaptureException(exception, eventContext);
            CaptureInvocation(context, startedAt, 500);
            throw;
        }
    }

    private void CaptureInvocation(FunctionContext context, DateTimeOffset startedAt, int statusCode)
    {
        _client.CaptureRequest(new DebugBundleRequestInfo
        {
            Method = "AZURE_FUNCTION",
            Path = context.FunctionDefinition.Name,
            RouteTemplate = context.FunctionDefinition.EntryPoint
        }, new DebugBundleResponseInfo
        {
            StatusCode = statusCode,
            Duration = DateTimeOffset.UtcNow - startedAt
        }, BuildContext(context));
    }

    private static Dictionary<string, object?> BuildScope(FunctionContext context)
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["function_name"] = context.FunctionDefinition.Name,
            ["invocation_id"] = context.InvocationId,
            ["traceparent"] = context.TraceContext.TraceParent
        };
    }

    internal static Dictionary<string, object?> BuildContext(FunctionContext context)
    {
        var trigger = context.FunctionDefinition.InputBindings.Values.FirstOrDefault(binding => binding.Type.EndsWith("Trigger", StringComparison.OrdinalIgnoreCase));
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["framework"] = "azure-functions-isolated",
            ["function_name"] = context.FunctionDefinition.Name,
            ["function_id"] = context.FunctionId,
            ["entry_point"] = context.FunctionDefinition.EntryPoint,
            ["invocation_id"] = context.InvocationId,
            ["trigger_type"] = trigger?.Type,
            ["retry_count"] = context.RetryContext.RetryCount,
            ["max_retry_count"] = context.RetryContext.MaxRetryCount,
            ["traceparent"] = context.TraceContext.TraceParent,
            ["tracestate"] = context.TraceContext.TraceState,
            ["binding_data_keys"] = context.BindingContext.BindingData.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray(),
            ["input_bindings"] = SummarizeBindings(context.FunctionDefinition.InputBindings),
            ["output_bindings"] = SummarizeBindings(context.FunctionDefinition.OutputBindings)
        };
    }

    private static IReadOnlyList<Dictionary<string, object?>> SummarizeBindings(IReadOnlyDictionary<string, BindingMetadata> bindings)
    {
        var result = new List<Dictionary<string, object?>>(bindings.Count);
        foreach (var binding in bindings.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            result.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = binding.Value.Name,
                ["type"] = binding.Value.Type,
                ["direction"] = binding.Value.Direction.ToString()
            });
        }

        return result;
    }
}
