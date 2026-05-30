using DebugBundle;

var client = DebugBundleClient.Create(new DebugBundleOptions
{
    Enabled = false,
    ProjectToken = "smoke-token",
    Service = "dotnet-smoke"
});

client.CaptureMessage("clean install smoke");

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

Console.WriteLine(string.Join(",", exportedTypes.Select(type => type.FullName)));
