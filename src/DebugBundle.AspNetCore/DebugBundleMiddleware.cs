using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DebugBundle.AspNetCore;

public sealed class DebugBundleMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IDebugBundleClient _client;

    public DebugBundleMiddleware(RequestDelegate next, IDebugBundleClient client)
    {
        _next = next;
        _client = client;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        var scopeValues = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["request_id"] = FirstNonEmpty(context.Request.Headers["X-Request-ID"].FirstOrDefault(), context.Request.Headers["X-Correlation-ID"].FirstOrDefault(), context.TraceIdentifier),
            ["trace_id"] = context.Request.Headers["X-DebugBundle-Trace-Id"].FirstOrDefault(),
            ["probe_trigger_token"] = FirstNonEmpty(context.Request.Headers["X-DebugBundle-Probe-Trigger"].FirstOrDefault(), context.Request.Query["_debug_probe"].FirstOrDefault()),
            ["activity_trace_id"] = Activity.Current?.TraceId.ToString()
        };

        using var scope = _client.BeginScope(scopeValues);
        var capturedRequest = false;
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            _client.CaptureException(exception, BuildEndpointContext(context));
            _client.CaptureRequest(BuildRequestInfo(context), BuildResponseInfo(context, stopwatch.Elapsed), BuildEndpointContext(context));
            capturedRequest = true;
            throw;
        }
        finally
        {
            stopwatch.Stop();
            if (!capturedRequest && !context.Response.HasStarted)
            {
                _client.CaptureRequest(BuildRequestInfo(context), BuildResponseInfo(context, stopwatch.Elapsed), BuildEndpointContext(context));
            }
        }
    }

    private static DebugBundleRequestInfo BuildRequestInfo(HttpContext context)
    {
        return new DebugBundleRequestInfo
        {
            Method = context.Request.Method,
            Path = context.Request.Path.HasValue ? context.Request.Path.Value! : "/",
            RouteTemplate = RouteTemplate(context),
            Headers = context.Request.Headers.ToDictionary(item => item.Key, item => item.Value.ToString(), StringComparer.OrdinalIgnoreCase),
            Query = context.Request.Query.ToDictionary(item => item.Key, item => (string?)item.Value.ToString(), StringComparer.OrdinalIgnoreCase)
        };
    }

    private static DebugBundleResponseInfo BuildResponseInfo(HttpContext context, TimeSpan duration)
    {
        return new DebugBundleResponseInfo
        {
            StatusCode = context.Response.StatusCode,
            Duration = duration,
            Headers = context.Response.Headers.ToDictionary(item => item.Key, item => item.Value.ToString(), StringComparer.OrdinalIgnoreCase)
        };
    }

    private static IDictionary<string, object?> BuildEndpointContext(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (endpoint?.DisplayName != null)
        {
            result["endpoint"] = endpoint.DisplayName;
        }

        if (endpoint is RouteEndpoint routeEndpoint)
        {
            result["route_template"] = routeEndpoint.RoutePattern.RawText;
        }

        return result;
    }

    private static string? RouteTemplate(HttpContext context)
    {
        return (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}
