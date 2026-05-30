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

    private sealed class TestNLogTarget : DebugBundleTarget
    {
        public TestNLogTarget(IDebugBundleClient client)
            : base(client)
        {
        }

        public void WriteForTest(LogEventInfo logEvent) => Write(logEvent);
    }
}
