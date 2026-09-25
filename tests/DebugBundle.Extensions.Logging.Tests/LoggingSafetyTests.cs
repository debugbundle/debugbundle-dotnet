using DebugBundle.Transport;
using DebugBundle.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DebugBundle.Extensions.Logging.Tests;

public sealed class LoggingSafetyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HeldFormatterOrExceptionGetterDoesNotHoldLoggingCaller(bool holdException)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var transport = new Transport();
        using var client = DebugBundleClient.Create(new DebugBundleOptions
        {
            ProjectMode = DebugBundleProjectMode.LocalOnly,
            Transport = transport,
            BatchSize = 1,
            RequestTimeout = TimeSpan.FromMilliseconds(100),
            FlushInterval = TimeSpan.FromHours(1)
        });
        var services = new ServiceCollection();
        services.AddSingleton<IDebugBundleClient>(client);
        services.AddLogging(builder => builder.AddDebugBundle());
        using var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Safety");
        var state = new object();
        var exception = holdException ? new HeldException(entered, release) : null;
        Func<object, Exception?, string> formatter = (_, _) =>
        {
            if (!holdException) { entered.Set(); release.Wait(); }
            return "formatted token=private";
        };
        var capture = Task.Run(() => logger.Log(LogLevel.Error, new EventId(3), state, exception, formatter));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            Assert.Same(capture, await Task.WhenAny(capture, Task.Delay(250)));
            await client.FlushAsync().WaitAsync(TimeSpan.FromMilliseconds(500));
        }
        finally { release.Set(); }
        await capture;
        await client.FlushAsync();
        await transport.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        GC.KeepAlive(state); GC.KeepAlive(formatter); GC.KeepAlive(exception);
        var envelope = Assert.Single(transport.Events);
        Assert.Equal("formatted token=[REDACTED]", envelope.Payload["message"]);
        if (holdException)
        {
            var attributes = Assert.IsType<Dictionary<string, object?>>(envelope.Payload["attributes"]);
            var detail = Assert.IsType<Dictionary<string, object?>>(attributes["exception"]);
            Assert.Equal("exception token=[REDACTED]", detail["message"]);
        }
    }

    [Fact]
    public void CustomValueStateAndTemplateArgumentsDoNotInvokeApplicationFormatting()
    {
        var fake = new FakeClient();
        var services = new ServiceCollection();
        services.AddSingleton<IDebugBundleClient>(fake);
        services.AddLogging(builder => builder.AddDebugBundle());
        using var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Safety");
        var called = false;
        logger.Log(LogLevel.Error, default, 42, null, (_, _) => { called = true; return "unexpected"; });
        logger.LogError("Item {Item}", new UnsupportedValue());
        logger.LogError("Value {Value,100000000}", 1);
        logger.LogError("Value {Value:N999999999}", 1);
        Assert.Equal("Log details unavailable", fake.Logs[2].Message);
        Assert.Equal("Log details unavailable", fake.Logs[3].Message);
        Assert.False(called);
        Assert.Equal("Log details unavailable", fake.Logs[0].Message);
        Assert.Equal("Item [Unsupported value]", fake.Logs[1].Message);
    }

    private sealed class UnsupportedValue { public override string ToString() => throw new InvalidOperationException("host formatter"); }
    private sealed class HeldException(ManualResetEventSlim entered, ManualResetEventSlim release) : Exception
    {
        public override string Message { get { entered.Set(); release.Wait(); return "exception token=private"; } }
    }
    private sealed class Transport : IEventTransport
    {
        internal TaskCompletionSource<bool> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal List<DebugBundleEventEnvelope> Events { get; } = new();
        public Task<EventTransportResult> SendAsync(EventTransportRequest request, CancellationToken cancellationToken = default)
        {
            Events.AddRange(request.Events);
            Completed.TrySetResult(true);
            return Task.FromResult(new EventTransportResult { StatusCode = 202 });
        }
    }
}
