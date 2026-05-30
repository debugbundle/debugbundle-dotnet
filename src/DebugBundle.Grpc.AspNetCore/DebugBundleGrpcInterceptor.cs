using Grpc.Core;
using Grpc.Core.Interceptors;

namespace DebugBundle.Grpc;

public sealed class DebugBundleGrpcInterceptor : Interceptor
{
    private readonly IDebugBundleClient _client;

    public DebugBundleGrpcInterceptor(IDebugBundleClient client)
    {
        _client = client;
    }

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            var response = await continuation(request, context).ConfigureAwait(false);
            CaptureRequest(context, StatusCode.OK, startedAt);
            return response;
        }
        catch (RpcException exception)
        {
            CaptureException(context, exception, exception.StatusCode, startedAt);
            throw;
        }
        catch (Exception exception)
        {
            CaptureException(context, exception, StatusCode.Unknown, startedAt);
            throw;
        }
    }

    public override async Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            var response = await continuation(requestStream, context).ConfigureAwait(false);
            CaptureRequest(context, StatusCode.OK, startedAt);
            return response;
        }
        catch (RpcException exception)
        {
            CaptureException(context, exception, exception.StatusCode, startedAt);
            throw;
        }
        catch (Exception exception)
        {
            CaptureException(context, exception, StatusCode.Unknown, startedAt);
            throw;
        }
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            await continuation(request, responseStream, context).ConfigureAwait(false);
            CaptureRequest(context, StatusCode.OK, startedAt);
        }
        catch (RpcException exception)
        {
            CaptureException(context, exception, exception.StatusCode, startedAt);
            throw;
        }
        catch (Exception exception)
        {
            CaptureException(context, exception, StatusCode.Unknown, startedAt);
            throw;
        }
    }

    public override async Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            await continuation(requestStream, responseStream, context).ConfigureAwait(false);
            CaptureRequest(context, StatusCode.OK, startedAt);
        }
        catch (RpcException exception)
        {
            CaptureException(context, exception, exception.StatusCode, startedAt);
            throw;
        }
        catch (Exception exception)
        {
            CaptureException(context, exception, StatusCode.Unknown, startedAt);
            throw;
        }
    }

    private void CaptureException(ServerCallContext context, Exception exception, StatusCode statusCode, DateTimeOffset startedAt)
    {
        _client.CaptureException(exception, BuildContext(context, statusCode, startedAt));
        CaptureRequest(context, statusCode, startedAt);
    }

    private void CaptureRequest(ServerCallContext context, StatusCode statusCode, DateTimeOffset startedAt)
    {
        _client.CaptureRequest(new DebugBundleRequestInfo
        {
            Method = "GRPC",
            Path = context.Method,
            RouteTemplate = context.Method,
            Headers = MetadataToHeaders(context.RequestHeaders)
        }, new DebugBundleResponseInfo
        {
            StatusCode = GrpcStatusToHttpStatus(statusCode),
            Duration = DateTimeOffset.UtcNow - startedAt
        }, BuildContext(context, statusCode, startedAt));
    }

    private static Dictionary<string, object?> BuildContext(ServerCallContext context, StatusCode statusCode, DateTimeOffset startedAt)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["grpc.method"] = context.Method,
            ["grpc.host"] = context.Host,
            ["grpc.peer"] = context.Peer,
            ["grpc.status_code"] = statusCode.ToString(),
            ["grpc.deadline"] = context.Deadline == DateTime.MaxValue ? null : context.Deadline.ToUniversalTime().ToString("O"),
            ["started_at"] = startedAt.ToString("O")
        };
        if (TryHeader(context.RequestHeaders, "x-debugbundle-trace-id", out var traceId))
        {
            values["trace_id"] = traceId;
        }

        if (TryHeader(context.RequestHeaders, "x-request-id", out var requestId))
        {
            values["request_id"] = requestId;
        }

        return values;
    }

    private static Dictionary<string, string> MetadataToHeaders(Metadata metadata)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "x-debugbundle-trace-id", "x-request-id", "x-correlation-id", "traceparent" })
        {
            if (TryHeader(metadata, key, out var value))
            {
                result[key] = value;
            }
        }

        return result;
    }

    private static bool TryHeader(Metadata metadata, string key, out string value)
    {
        foreach (var entry in metadata)
        {
            if (entry.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(entry.Value))
            {
                value = entry.Value;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static int GrpcStatusToHttpStatus(StatusCode statusCode)
    {
        return statusCode == StatusCode.OK ? 200 : 500;
    }
}
