using System.Diagnostics;
using System.Runtime.CompilerServices;
using DebugBundle;
using DebugBundle.Transport;

namespace DebugBundle.Sdk.Tests;

public sealed class ExceptionProjectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HeldVirtualExceptionDetailsDoNotHoldCaptureOrFlush(bool holdToString)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var exception = new HeldException(entered, release, holdToString);
        var transport = new FakeTransport();
        using var client = CreateClient(transport, shortWait: true);
        var capture = Task.Run(() => client.CaptureException(exception));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            Assert.Same(capture, await Task.WhenAny(capture, Task.Delay(250)));
            for (var index = 0; index < 512; index++) client.CaptureException(exception);
            var timer = Stopwatch.StartNew();
            for (var index = 0; index < 10_000; index++) client.CaptureException(exception);
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2));
            await client.FlushAsync().WaitAsync(TimeSpan.FromMilliseconds(500));
        }
        finally { release.Set(); }
        await capture;
        await client.FlushAsync();
        GC.KeepAlive(exception);
        var events = transport.Batches.SelectMany(batch => batch).Where(item => item.EventType == "backend_exception");
        Assert.Contains(events, item => (string)item.Payload["message"]! == "held message token=[REDACTED]");
    }

    [Fact]
    public async Task OrdinaryExceptionMessageStackInnerAndHResultSurviveDeferredProjection()
    {
        var transport = new FakeTransport();
        using var client = CreateClient(transport);
        var inner = new ArgumentException("inner token=private");
        var error = new InvalidOperationException("outer token=private", inner);
        try { throw error; } catch (Exception captured) { client.CaptureException(captured); }
        await client.FlushAsync();
        GC.KeepAlive(error);
        var envelope = Assert.Single(transport.Batches.SelectMany(batch => batch));
        Assert.Equal("outer token=[REDACTED]", envelope.Payload["message"]);
        Assert.Contains(nameof(OrdinaryExceptionMessageStackInnerAndHResultSurviveDeferredProjection), (string)envelope.Payload["stack"]!);
        Assert.Equal(error.HResult, envelope.Context!["exception_hresult"]);
        var summary = Assert.IsType<Dictionary<string, object?>>(envelope.Context["inner_exception"]);
        Assert.Equal("inner token=[REDACTED]", summary["message"]);
    }

    [Fact]
    public async Task QueuedExceptionsDoNotStronglyRetainApplicationGraphs()
    {
        var transport = new FakeTransport();
        using var client = DebugBundleClient.Create(new DebugBundleOptions
        {
            ProjectMode = DebugBundleProjectMode.LocalOnly,
            Transport = transport,
            BatchSize = 512,
            FlushInterval = TimeSpan.FromHours(1)
        });
        var weak = CaptureTemporary(client);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.False(weak.IsAlive);
        await client.FlushAsync();
        var envelope = Assert.Single(transport.Batches.SelectMany(batch => batch));
        Assert.Equal("ordinary snapshot", envelope.Payload["message"]);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CaptureTemporary(DebugBundleClient client)
    {
        var error = new InvalidOperationException("ordinary snapshot");
        client.CaptureException(error);
        return new WeakReference(error);
    }

    [Fact]
    public async Task HeldSamplingCallbackDoesNotHoldCaptureAndSampledOutEventsSkipHooks()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var calls = 0;
        using var client = DebugBundleClient.Create(new DebugBundleOptions
        {
            ProjectMode = DebugBundleProjectMode.LocalOnly,
            Transport = new FakeTransport(),
            BatchSize = 1,
            RequestTimeout = TimeSpan.FromMilliseconds(100),
            SampleRate = 0.5,
            RandomSource = () => { entered.Set(); release.Wait(); return 1; },
            BeforeSend = envelope => { Interlocked.Increment(ref calls); return envelope; }
        });
        var capture = Task.Run(() => client.CaptureLog("sample", DebugBundleLogLevel.Error));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            Assert.Same(capture, await Task.WhenAny(capture, Task.Delay(250)));
            await client.FlushAsync().WaitAsync(TimeSpan.FromMilliseconds(500));
        }
        finally { release.Set(); }
        await capture;
        await client.FlushAsync();
        Assert.Equal(0, calls);
    }

    private static DebugBundleClient CreateClient(IEventTransport transport, bool shortWait = false) => DebugBundleClient.Create(new DebugBundleOptions
    {
        ProjectMode = DebugBundleProjectMode.LocalOnly,
        Transport = transport,
        BatchSize = 1,
        RequestTimeout = shortWait ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(5),
        FlushInterval = TimeSpan.FromHours(1)
    });

    private sealed class HeldException(ManualResetEventSlim entered, ManualResetEventSlim release, bool holdToString) : Exception("held message token=private")
    {
        public override string Message { get { if (!holdToString) { entered.Set(); release.Wait(); } return base.Message; } }
        public override string ToString() { if (holdToString) { entered.Set(); release.Wait(); } return base.ToString(); }
    }
}
