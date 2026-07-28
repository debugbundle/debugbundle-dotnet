using DebugBundle.Log4Net;
using DebugBundle.NLog;
using DebugBundle.Serilog;
using log4net.Core;
using NLog;
using Serilog.Events;
using Serilog.Parsing;

namespace DebugBundle.LoggingAdapters.Tests;

public sealed class LoggingAdapterTests
{
    [Fact]
    public void Serilog_Sink_Captures_Log_And_Exception()
    {
        var client = new FakeClient();
        var sink = new DebugBundleSink(client);
        var exception = new InvalidOperationException("failed");
        var logEvent = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Error,
            exception,
            new MessageTemplate("Payment failed for {OrderId}", new MessageTemplateToken[] { new TextToken("Payment failed for "), new PropertyToken("OrderId", "{OrderId}") }),
            new[] { new LogEventProperty("OrderId", new ScalarValue("ord-123")) });

        sink.Emit(logEvent);

        Assert.Single(client.Exceptions);
        var log = Assert.Single(client.Logs);
        Assert.Equal(DebugBundleLogLevel.Error, log.Level);
        Assert.Equal("Payment failed for \"ord-123\"", log.Message);
        Assert.Equal("serilog", log.Context!["logger"]);
    }

    [Fact]
    public void Adapters_Support_Configuration_Constructors()
    {
        _ = new DebugBundleSink();
        _ = new DebugBundleTarget();
        _ = new DebugBundleAppender();
    }

    [Fact]
    public void NLog_Target_Captures_Log()
    {
        var client = new FakeClient();
        var target = new TestNLogTarget(client);
        var logEvent = new LogEventInfo(LogLevel.Warn, "billing", "retrying charge");
        logEvent.Properties["attempt"] = 2;

        target.WriteForTest(logEvent);

        var log = Assert.Single(client.Logs);
        Assert.Equal(DebugBundleLogLevel.Warning, log.Level);
        Assert.Equal("retrying charge", log.Message);
        Assert.Equal("nlog", log.Context!["provider"]);
    }

    [Fact]
    public void Log4Net_Appender_Captures_Log()
    {
        var client = new FakeClient();
        var appender = new DebugBundleAppender(client);
        var loggingEvent = new LoggingEvent(new LoggingEventData
        {
            Level = Level.Error,
            LoggerName = "checkout",
            Message = "payment failed",
            TimeStampUtc = DateTime.UtcNow,
            ThreadName = "test"
        });

        appender.DoAppend(loggingEvent);

        var log = Assert.Single(client.Logs);
        Assert.Equal(DebugBundleLogLevel.Error, log.Level);
        Assert.Equal("payment failed", log.Message);
        Assert.Equal("log4net", log.Context!["provider"]);
    }

    [Fact]
    public void Adapters_Map_All_Levels_And_Swallow_Client_Failures()
    {
        var serilogClient = new FakeClient();
        var serilog = new DebugBundleSink(serilogClient);
        foreach (var level in Enum.GetValues<LogEventLevel>())
        {
            serilog.Emit(new LogEvent(
                DateTimeOffset.UtcNow,
                level,
                null,
                new MessageTemplate("level", new MessageTemplateToken[] { new TextToken("level") }),
                Array.Empty<LogEventProperty>()));
        }
        serilog.Emit(null!);
        new DebugBundleSink(new FakeClient { ThrowOnCapture = true }).Emit(new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Error,
            new InvalidOperationException("failed"),
            new MessageTemplate("failed", new MessageTemplateToken[] { new TextToken("failed") }),
            Array.Empty<LogEventProperty>()));
        Assert.Equal(6, serilogClient.Logs.Count);

        var nlogClient = new FakeClient();
        var nlog = new TestNLogTarget(nlogClient);
        foreach (var level in new[] { LogLevel.Trace, LogLevel.Debug, LogLevel.Info, LogLevel.Warn, LogLevel.Error, LogLevel.Fatal })
        {
            nlog.WriteForTest(new LogEventInfo(level, "levels", "level")
            {
                Exception = level == LogLevel.Error ? new InvalidOperationException("failed") : null
            });
        }
        nlog.WriteForTest(null!);
        new TestNLogTarget(new FakeClient { ThrowOnCapture = true })
            .WriteForTest(new LogEventInfo(LogLevel.Error, "levels", "failed"));
        Assert.Equal(6, nlogClient.Logs.Count);
        Assert.Single(nlogClient.Exceptions);

        var log4NetClient = new FakeClient();
        var appender = new DebugBundleAppender(log4NetClient);
        foreach (var level in new[] { Level.Trace, Level.Debug, Level.Info, Level.Warn, Level.Error, Level.Fatal })
        {
            appender.DoAppend(new LoggingEvent(
                typeof(LoggingAdapterTests),
                log4net.LogManager.GetRepository(),
                "levels",
                level,
                "level",
                level == Level.Error ? new InvalidOperationException("failed") : null));
        }
        appender.DoAppend((LoggingEvent)null!);
        new DebugBundleAppender(new FakeClient { ThrowOnCapture = true }).DoAppend(new LoggingEvent(
            typeof(LoggingAdapterTests),
            log4net.LogManager.GetRepository(),
            "levels",
            Level.Error,
            "failed",
            null));
        Assert.Equal(6, log4NetClient.Logs.Count);
        Assert.Single(log4NetClient.Exceptions);
    }

    private sealed class TestNLogTarget : DebugBundleTarget
    {
        public TestNLogTarget(IDebugBundleClient client)
            : base(client)
        {
        }

        public void WriteForTest(LogEventInfo logEvent) => Write(logEvent);
    }
}
