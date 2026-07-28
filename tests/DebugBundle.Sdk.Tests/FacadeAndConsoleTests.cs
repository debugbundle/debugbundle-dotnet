using System.Text;
using DebugBundle;

namespace DebugBundle.Sdk.Tests;

public sealed class FacadeAndConsoleTests
{
    [Fact]
    public async Task Static_Facade_Forwards_The_Full_Universal_Surface()
    {
        var transport = new FakeTransport();
        global::DebugBundle.DebugBundle.Init(new DebugBundleOptions
        {
            ProjectToken = "dbundle_proj_test",
            Service = "facade-test",
            Environment = "test",
            Transport = transport,
            BatchSize = 50,
            RandomSource = () => 0
        });

        global::DebugBundle.DebugBundle.SetContext("tenant", "tenant_123");
        global::DebugBundle.DebugBundle.SetUserHash("user_hash");
        global::DebugBundle.DebugBundle.SetTraceId("trace_123");
        global::DebugBundle.DebugBundle.SetRequestId("request_123");
        using (global::DebugBundle.DebugBundle.BeginScope(new Dictionary<string, object?> { ["scope"] = true }))
        {
            global::DebugBundle.DebugBundle.CaptureException(new InvalidOperationException("exception"));
            global::DebugBundle.DebugBundle.CaptureError(new ArgumentException("error"));
            global::DebugBundle.DebugBundle.CaptureLog("log", DebugBundleLogLevel.Error);
            global::DebugBundle.DebugBundle.CaptureMessage("message", DebugBundleLogLevel.Warning);
            global::DebugBundle.DebugBundle.CaptureRequest(
                new DebugBundleRequestInfo { Method = "GET", Path = "/failed" },
                new DebugBundleResponseInfo { StatusCode = 503 });
            global::DebugBundle.DebugBundle.Probe("facade.value", 42);
            global::DebugBundle.DebugBundle.Probe("facade.lazy", () => new { Ready = true });
        }

        var adapter = new StaticDebugBundleClient();
        adapter.SetContext("adapter", true);
        adapter.SetUserHash("adapter_user");
        adapter.SetTraceId("adapter_trace");
        adapter.SetRequestId("adapter_request");
        using (adapter.BeginScope(new Dictionary<string, object?> { ["adapter_scope"] = true }))
        {
            adapter.CaptureException(new InvalidOperationException("adapter exception"));
            adapter.CaptureError(new ArgumentException("adapter error"));
            adapter.CaptureLog("adapter log", DebugBundleLogLevel.Error);
            adapter.CaptureMessage("adapter message");
            adapter.CaptureRequest(
                new DebugBundleRequestInfo { Method = "POST", Path = "/adapter" },
                new DebugBundleResponseInfo { StatusCode = 500 });
            adapter.Probe("adapter.value", 1);
            adapter.Probe("adapter.lazy", () => 2);
        }
        await adapter.FlushAsync();

        Assert.Equal(DebugBundleStatus.Healthy, global::DebugBundle.DebugBundle.Status);
        Assert.NotNull(global::DebugBundle.DebugBundle.LastEventAt);
        Assert.Equal(DebugBundleStatus.Healthy, adapter.Status);
        Assert.NotNull(adapter.LastEventAt);
        Assert.Contains(transport.Batches.Single(), item => item.EventType == "request_event");
    }

    [Fact]
    public async Task Exception_Capture_Wrappers_Cover_Success_Null_And_Rethrow_Paths()
    {
        var calls = 0;
        global::DebugBundle.DebugBundle.WithExceptionCapture(() => calls++);
        global::DebugBundle.DebugBundle.WithExceptionCapture(null!);
        Assert.Equal(42, global::DebugBundle.DebugBundle.WithExceptionCapture(() => 42));
        Assert.Equal(0, global::DebugBundle.DebugBundle.WithExceptionCapture<int>(null!));
        await global::DebugBundle.DebugBundle.WithExceptionCaptureAsync(() =>
        {
            calls++;
            return Task.CompletedTask;
        });
        await global::DebugBundle.DebugBundle.WithExceptionCaptureAsync(null!);
        Assert.Equal(
            43,
            await global::DebugBundle.DebugBundle.WithExceptionCaptureAsync(() => Task.FromResult(43)));
        Assert.Equal(
            0,
            await global::DebugBundle.DebugBundle.WithExceptionCaptureAsync<int>(null!));

        Assert.Throws<ApplicationException>(() =>
            global::DebugBundle.DebugBundle.WithExceptionCapture<int>(
                () => throw new ApplicationException("generic failure")));
        Assert.Throws<ApplicationException>(() =>
            global::DebugBundle.DebugBundle.WithExceptionCapture(
                () => throw new ApplicationException("failure")));
        await Assert.ThrowsAsync<ApplicationException>(() =>
            global::DebugBundle.DebugBundle.WithExceptionCaptureAsync(
                () => Task.FromException(new ApplicationException("async failure"))));
        await Assert.ThrowsAsync<ApplicationException>(() =>
            global::DebugBundle.DebugBundle.WithExceptionCaptureAsync<int>(
                () => throw new ApplicationException("async generic failure")));

        Assert.Equal(2, calls);
    }

    [Fact]
    public void Unhandled_Capture_Registration_Is_Idempotent()
    {
        global::DebugBundle.DebugBundle.CaptureUnhandledExceptions();
        global::DebugBundle.DebugBundle.CaptureUnhandledExceptions();
        global::DebugBundle.DebugBundle.CaptureTaskSchedulerExceptions();
        global::DebugBundle.DebugBundle.CaptureAppDomainExceptions();
    }

    [Fact]
    public void Console_Capture_Registration_Is_Idempotent_And_Additive()
    {
        var originalError = Console.Error;
        var originalOutput = Console.Out;
        try
        {
            global::DebugBundle.DebugBundle.CaptureConsoleLogs();
            global::DebugBundle.DebugBundle.CaptureConsoleLogs(includeStandardOutput: true);
            global::DebugBundle.DebugBundle.CaptureConsoleLogs(includeStandardOutput: true);
        }
        finally
        {
            Console.SetError(originalError);
            Console.SetOut(originalOutput);
        }
    }

    [Fact]
    public async Task Console_Writer_Preserves_Output_And_Captures_Nonblank_Lines()
    {
        var transport = new FakeTransport();
        var client = DebugBundleClient.Create(new DebugBundleOptions
        {
            ProjectToken = "dbundle_proj_test",
            Service = "console-test",
            Environment = "test",
            Transport = transport,
            RandomSource = () => 0
        });
        var output = new StringWriter();
        using (var writer = new DebugBundleConsoleWriter(
            output,
            DebugBundleLogLevel.Error,
            () => client))
        {
            Assert.Equal(Encoding.Unicode.WebName, writer.Encoding.WebName);
            writer.Write('a');
            writer.Write("b");
            writer.WriteLine("captured");
            await writer.WriteLineAsync("captured async");
            writer.WriteLine(" ");
        }

        await client.FlushAsync();

        Assert.Contains("abcaptured", output.ToString());
        var events = transport.Batches.Single();
        Assert.Equal(2, events.Count(item => item.EventType == "log_event"));
    }

    [Fact]
    public void Console_Writer_Swallows_Client_Failures_And_Missing_Clients()
    {
        using var missing = new DebugBundleConsoleWriter(
            new StringWriter(),
            DebugBundleLogLevel.Error,
            () => null);
        using var failing = new DebugBundleConsoleWriter(
            new StringWriter(),
            DebugBundleLogLevel.Error,
            () => throw new InvalidOperationException("accessor failed"));

        missing.WriteLine("missing");
        failing.WriteLine("failure");
    }
}
