using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DebugBundle;

var portProbe = new TcpListener(IPAddress.Loopback, 0);
portProbe.Start();
var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
portProbe.Stop();

using var listener = new HttpListener();
listener.Prefixes.Add($"http://127.0.0.1:{port}/");
listener.Start();

var receivedRequest = ReceiveBatchAsync(listener);
using var client = DebugBundleClient.Create(new DebugBundleOptions
{
    ProjectToken = "dbundle_proj_dotnet_smoke",
    Service = "dotnet-smoke",
    Environment = "smoke",
    Endpoint = new Uri($"http://127.0.0.1:{port}/v1/events"),
    BatchSize = 25,
    FlushInterval = TimeSpan.FromMinutes(1),
    RequestTimeout = TimeSpan.FromSeconds(5)
});

var traceId = "11111111111111111111111111111111";
client.CaptureException(
    new InvalidOperationException("clean install smoke exception"),
    new Dictionary<string, object?> { ["trace_id"] = traceId });
client.CaptureRequest(
    new DebugBundleRequestInfo
    {
        Method = "GET",
        Path = "/smoke",
        Query = new Dictionary<string, string?> { ["mode"] = "artifact" }
    },
    new DebugBundleResponseInfo
    {
        StatusCode = 503,
        Duration = TimeSpan.FromMilliseconds(25)
    },
    new Dictionary<string, object?> { ["trace_id"] = traceId });

await client.FlushAsync();
var captured = await receivedRequest.WaitAsync(TimeSpan.FromSeconds(10));

if (captured.Authorization != "Bearer dbundle_proj_dotnet_smoke")
{
    throw new InvalidOperationException("The installed SDK did not send the project token as bearer authorization.");
}

using var document = JsonDocument.Parse(captured.Body);
var events = document.RootElement.GetProperty("events");
if (events.GetArrayLength() != 2)
{
    throw new InvalidOperationException($"Expected two delivered events, received {events.GetArrayLength()}.");
}

var eventTypes = events.EnumerateArray()
    .Select(item => item.GetProperty("event_type").GetString())
    .ToHashSet(StringComparer.Ordinal);
if (!eventTypes.SetEquals(["backend_exception", "request_event"]))
{
    throw new InvalidOperationException("The installed SDK did not deliver the exception and request event.");
}

foreach (var item in events.EnumerateArray())
{
    if (item.GetProperty("service").GetProperty("name").GetString() != "dotnet-smoke" ||
        item.GetProperty("service").GetProperty("environment").GetString() != "smoke" ||
        item.GetProperty("sdk_name").GetString() != DebugBundleConstants.SdkName ||
        item.GetProperty("sdk_version").GetString() != DebugBundleConstants.SdkVersion ||
        item.GetProperty("correlation").GetProperty("trace_id").GetString() != traceId)
    {
        throw new InvalidOperationException("The installed SDK delivered an event with incorrect identity or correlation.");
    }
}

if (client.LastEventAt == null || client.Status != DebugBundleStatus.Healthy)
{
    throw new InvalidOperationException("The installed SDK did not acknowledge successful delivery.");
}

var exportedTypes = new[]
{
    typeof(DebugBundleClient),
    typeof(DebugBundle.AspNetCore.DebugBundleMiddleware),
    typeof(DebugBundle.AspNetCore.DebugBundleCircuitHandler),
    typeof(DebugBundle.AzureFunctions.DebugBundleFunctionsMiddleware),
    typeof(DebugBundle.Grpc.DebugBundleGrpcInterceptor),
    typeof(DebugBundle.Hangfire.DebugBundleHangfireFilter),
    typeof(DebugBundle.Logging.DebugBundleLoggerProvider),
    typeof(DebugBundle.Log4Net.DebugBundleAppender),
    typeof(DebugBundle.NLog.DebugBundleTarget),
    typeof(DebugBundle.Serilog.DebugBundleSink),
    typeof(DebugBundle.Worker.DebugBundleOperationContext)
};

Console.WriteLine(
    $"Clean installed package delivered {events.GetArrayLength()} acknowledged events; " +
    string.Join(",", exportedTypes.Select(type => type.FullName)));

static async Task<CapturedRequest> ReceiveBatchAsync(HttpListener listener)
{
    HttpListenerContext context;
    do
    {
        context = await listener.GetContextAsync();
        if (context.Request.HttpMethod == "POST" && context.Request.Url?.AbsolutePath == "/v1/events")
        {
            break;
        }
        context.Response.StatusCode = (int)HttpStatusCode.NotFound;
        context.Response.Close();
    }
    while (true);

    using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
    var body = await reader.ReadToEndAsync();
    var eventCount = JsonDocument.Parse(body).RootElement.GetProperty("events").GetArrayLength();
    var acknowledgement = JsonSerializer.Serialize(new
    {
        accepted = eventCount,
        rejected = 0,
        errors = Array.Empty<object>()
    });
    var responseBytes = Encoding.UTF8.GetBytes(acknowledgement);
    context.Response.StatusCode = (int)HttpStatusCode.Accepted;
    context.Response.ContentType = "application/json";
    context.Response.ContentLength64 = responseBytes.Length;
    await context.Response.OutputStream.WriteAsync(responseBytes);
    context.Response.Close();
    return new CapturedRequest(context.Request.Headers["Authorization"], body);
}

internal sealed record CapturedRequest(string? Authorization, string Body);
