using System.Diagnostics;
using System.Reflection;
using System.Collections.Concurrent;
using System.Text.Json;
using DebugBundle;
using DebugBundle.Transport;

namespace DebugBundle.Sdk.Tests;

public sealed class CaptureSafetyTests
{
    private static Task WaitForSenderAsync(DebugBundleClient client)
    {
        // FlushAsync is an advisory bounded wait; delivery assertions observe sender completion.
        var completion = (TaskCompletionSource<bool>)typeof(DebugBundleClient)
            .GetField("_senderCompletion", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(client)!;
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task HookReplacedEventIdsDoNotMergeRetryOwnership()
    {
        var transport = new FakeTransport();
        transport.EnqueueResponse(new EventTransportResult { StatusCode = 503, RetryAfter = TimeSpan.FromHours(1) });
        var replacementId = Guid.NewGuid().ToString();
        using var client = DebugBundleClient.Create(new DebugBundleOptions
        {
            ProjectToken = "dbundle_proj_test",
            Environment = "test",
            Transport = transport,
            RemoteConfigFetcher = new FakeRemoteConfigFetcher(),
            BeforeSend = envelope => { envelope.EventId = replacementId; return envelope; }
        });
        client.CaptureLog("first", DebugBundleLogLevel.Error);
        client.CaptureLog("second with a different retained size", DebugBundleLogLevel.Error);
        await client.FlushAsync();

        Assert.All(transport.Batches.Single(), envelope => Assert.Equal(replacementId, envelope.EventId));
        var pending = (List<DebugBundleEventEnvelope>)typeof(DebugBundleClient)
            .GetField("_buffer", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(client)!;
        Assert.Equal(2, pending.Count);
        Assert.Equal(pending.Sum(envelope => JsonSerializer.SerializeToUtf8Bytes(envelope).Length),
            typeof(DebugBundleClient).GetField("_queuedBytes", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(client));
    }

    [Fact]
    public async Task HookExpansionAndLaterCapturesShareTheOwnedByteBudget()
    {
        var transport = new HeldTransport();
        using var client = DebugBundleClient.Create(new DebugBundleOptions
        {
            ProjectToken = "dbundle_proj_test",
            Environment = "test",
            Transport = transport,
            RemoteConfigFetcher = new FakeRemoteConfigFetcher(),
            BatchSize = 150,
            BeforeSend = envelope =>
            {
                envelope.Context = Enumerable.Range(0, 20).ToDictionary(
                    index => $"field_{index}", _ => (object?)new string('x', 4096));
                return envelope;
            }
        });
        for (var index = 0; index < 150; index++)
            client.CaptureLog($"expanded error {index}", DebugBundleLogLevel.Error);
        await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        for (var index = 0; index < 512; index++)
            client.CaptureException(new InvalidOperationException($"later incident {index}"));

        var pending = (List<DebugBundleEventEnvelope>)typeof(DebugBundleClient)
            .GetField("_buffer", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(client)!;
        var sent = transport.Batches.First();
        var heldBytes = sent.Sum(envelope => JsonSerializer.SerializeToUtf8Bytes(envelope).Length);
        var pendingBytes = pending.Sum(envelope => JsonSerializer.SerializeToUtf8Bytes(envelope).Length);
        Assert.InRange(heldBytes + pendingBytes, 1, 8 * 1024 * 1024);
        Assert.Equal(heldBytes, typeof(DebugBundleClient)
            .GetField("_inFlightBytes", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(client));
        Assert.True(sent.Count(envelope => envelope.EventType == "log_event") < 150);

        transport.Release();
        await client.FlushAsync();
        Assert.All(transport.Batches, events => Assert.InRange(
            events.Sum(envelope => JsonSerializer.SerializeToUtf8Bytes(envelope).Length), 1, 8 * 1024 * 1024));
    }

    [Fact]
    public void FilteredInfoBurstDoesNotRunHook()
    {
        var hooks = 0;
        using var client = DebugBundleClient.Create(new DebugBundleOptions
        {
            ProjectToken = "dbundle_proj_test",
            Environment = "test",
            Transport = new FakeTransport(),
            RemoteConfigFetcher = new FakeRemoteConfigFetcher(),
            LogLevel = DebugBundleLogLevel.Warning,
            BeforeSend = envelope => { Interlocked.Increment(ref hooks); return envelope; }
        });

        for (var index = 0; index < 10_000; index++)
            client.CaptureLog($"filtered INFO {index}", DebugBundleLogLevel.Information);

        Assert.Equal(0, hooks);
    }

    [Fact]
    public void ABlockingConfigFetcherDoesNotBlockConstruction()
    {
        var fetcher = new BlockingConfigFetcher();
        var started = Stopwatch.StartNew();
        using var client = DebugBundleClient.Create(new DebugBundleOptions
        {
            ProjectToken = "dbundle_proj_test",
            Environment = "test",
            Transport = new FakeTransport(),
            RemoteConfigFetcher = fetcher
        });

        Assert.True(started.Elapsed < TimeSpan.FromMilliseconds(250));
    }

    [Fact]
    public void ThrowingExceptionGetterDoesNotEscapeIntoHost()
    {
        using var client = DebugBundleClient.Create(new DebugBundleOptions
        {
            ProjectToken = "dbundle_proj_test",
            Environment = "test",
            Transport = new FakeTransport(),
            RemoteConfigFetcher = new FakeRemoteConfigFetcher()
        });

        var escaped = Record.Exception(() => client.CaptureException(new ThrowingMessageException()));
        Assert.Null(escaped);
    }

    [Fact]
    public async Task HeldTransportHasOnlyOneSendInFlight()
    {
        var transport = new HeldTransport();
        using var client = DebugBundleClient.Create(new DebugBundleOptions
        {
            ProjectToken = "dbundle_proj_test",
            Environment = "test",
            Transport = transport,
            RemoteConfigFetcher = new FakeRemoteConfigFetcher(),
            BatchSize = 1
        });

        client.CaptureLog("first", DebugBundleLogLevel.Error);
        await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        for (var index = 0; index < 20; index++)
            client.CaptureLog($"later {index}", DebugBundleLogLevel.Error);

        Assert.Equal(1, transport.Calls);
        transport.Release();
        await client.FlushAsync();
    }

    [Fact]
    public async Task HeldBatchAndPendingQueueShareOneBudgetWithIncidentReserve()
    {
        var transport = new HeldTransport();
        using var client = DebugBundleClient.Create(new DebugBundleOptions
        {
            ProjectToken = "dbundle_proj_test",
            Environment = "test",
            Transport = transport,
            RemoteConfigFetcher = new FakeRemoteConfigFetcher(),
            BatchSize = 512
        });
        await client.InitialRemoteConfigTask;
        for (var index = 0; index < 512; index++)
            client.CaptureLog($"initial warning {index}", DebugBundleLogLevel.Warning);
        await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        for (var index = 0; index < 1_000; index++)
            client.CaptureLog($"later warning {index}", DebugBundleLogLevel.Warning);

        client.CaptureException(new InvalidOperationException("incident reserve"));
        var pending = (List<DebugBundleEventEnvelope>)typeof(DebugBundleClient)
            .GetField("_buffer", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(client)!;
        var retained = transport.Batches.First().Concat(pending).ToArray();
        Assert.True(retained.Length <= 1_000);
        Assert.True(retained.Count(envelope => envelope.EventType == "log_event") <= 800);
        Assert.Contains(retained, envelope => envelope.EventType == "backend_exception" &&
            Equals(envelope.Payload.GetValueOrDefault("message"), "incident reserve"));

        transport.Release();
        await client.FlushAsync();
    }

    [Fact]
    public async Task FullPendingQueueKeepsTheExceptionAndBoundsCardinality()
    {
        var transport = new HeldTransport();
        using var client = DebugBundleClient.Create(new DebugBundleOptions
        {
            ProjectToken = "dbundle_proj_test",
            Environment = "test",
            Transport = transport,
            RemoteConfigFetcher = new FakeRemoteConfigFetcher(),
            BatchSize = 1
        });
        client.CaptureLog("first warning", DebugBundleLogLevel.Warning);
        await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        client.CaptureException(new InvalidOperationException("retain this failure"));
        for (var index = 0; index < 1_100; index++)
            client.CaptureLog($"warning {index}", DebugBundleLogLevel.Warning);

        var pending = (List<DebugBundleEventEnvelope>)typeof(DebugBundleClient)
            .GetField("_buffer", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(client)!;
        Assert.True(pending.Count <= 1_000);
        Assert.Contains(pending, eventEnvelope => eventEnvelope.EventType == "backend_exception");
        transport.Release();
        await client.FlushAsync();
    }

    [Fact]
    public async Task ExceptionDisplacesLowPriorityLogsInAFullQueue()
    {
        var transport = new HeldTransport();
        using var client = DebugBundleClient.Create(new DebugBundleOptions
        {
            ProjectToken = "dbundle_proj_test",
            Environment = "test",
            Transport = transport,
            RemoteConfigFetcher = new FakeRemoteConfigFetcher(),
            BatchSize = 1
        });
        client.CaptureLog("held", DebugBundleLogLevel.Warning);
        await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        for (var index = 0; index < 512; index++)
            client.CaptureLog($"pending warning {index}", DebugBundleLogLevel.Warning);

        client.CaptureException(new InvalidOperationException("priority incident"));
        var pending = (List<DebugBundleEventEnvelope>)typeof(DebugBundleClient)
            .GetField("_buffer", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(client)!;
        Assert.Equal(512, pending.Count);
        Assert.Contains(pending, envelope => envelope.EventType == "backend_exception" &&
            Equals(envelope.Payload.GetValueOrDefault("message"), "priority incident"));

        transport.Release();
        await client.FlushAsync();
        await WaitForSenderAsync(client);
        Assert.Single(transport.Batches.SelectMany(batch => batch), envelope =>
            envelope.EventType == "error_suppressed" &&
            Equals(envelope.Payload.GetValueOrDefault("reason"), "queue_pressure"));
    }

    [Fact]
    public async Task FailedRequestDisplacesOrdinaryRequestsInAFullQueue()
    {
        var transport = new HeldTransport();
        var fetcher = new FakeRemoteConfigFetcher();
        fetcher.Responses.Enqueue(_ => new RemoteConfigFetchResult
        {
            Config = new SdkRemoteConfig
            {
                CapturePolicy = new CapturePolicy { CaptureRequestEvents = "all" }
            }
        });
        using var client = DebugBundleClient.Create(new DebugBundleOptions
        {
            ProjectToken = "dbundle_proj_test",
            Environment = "test",
            Transport = transport,
            RemoteConfigFetcher = fetcher,
            BatchSize = 1
        });
        await client.InitialRemoteConfigTask;
        client.CaptureLog("held", DebugBundleLogLevel.Warning);
        await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        for (var index = 0; index < 512; index++)
            client.CaptureRequest(
                new DebugBundleRequestInfo { Method = "GET", Path = $"/ordinary/{index}" },
                new DebugBundleResponseInfo { StatusCode = 200 });

        client.CaptureRequest(
            new DebugBundleRequestInfo { Method = "GET", Path = "/failed" },
            new DebugBundleResponseInfo { StatusCode = 503 });
        var pending = (List<DebugBundleEventEnvelope>)typeof(DebugBundleClient)
            .GetField("_buffer", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(client)!;
        Assert.Equal(512, pending.Count);
        Assert.Contains(pending, envelope => envelope.EventType == "request_event" &&
            Equals(envelope.Payload.GetValueOrDefault("response_status"), 503));

        transport.Release();
        await client.FlushAsync();
    }

    [Fact]
    public async Task ExplicitFlushTimesOutWithoutHoldingTheHost()
    {
        var transport = new HeldTransport();
        using var client = DebugBundleClient.Create(new DebugBundleOptions
        {
            ProjectToken = "dbundle_proj_test",
            Environment = "test",
            Transport = transport,
            RemoteConfigFetcher = new FakeRemoteConfigFetcher(),
            BatchSize = 1,
            RequestTimeout = TimeSpan.FromMilliseconds(40)
        });
        client.CaptureLog("held", DebugBundleLogLevel.Error);
        await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var elapsed = Stopwatch.StartNew();
        await client.FlushAsync();
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Equal(1, transport.Calls);
        transport.Release();
        await client.FlushAsync();
    }

    [Fact]
    public async Task ExplicitFlushHonorsCallerCancellation()
    {
        var transport = new HeldTransport();
        using var client = DebugBundleClient.Create(new DebugBundleOptions
        {
            ProjectToken = "dbundle_proj_test",
            Environment = "test",
            Transport = transport,
            RemoteConfigFetcher = new FakeRemoteConfigFetcher(),
            BatchSize = 1
        });
        client.CaptureLog("held", DebugBundleLogLevel.Error);
        await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.FlushAsync(cancelled.Token));
        transport.Release();
        await client.FlushAsync();
    }

    [Fact]
    public async Task HeldSenderDoesNotRetainUnboundedExplicitFlushTimers()
    {
        var transport = new HeldTransport();
        using var client = DebugBundleClient.Create(new DebugBundleOptions
        {
            ProjectToken = "dbundle_proj_test",
            Environment = "test",
            Transport = transport,
            RemoteConfigFetcher = new FakeRemoteConfigFetcher(),
            BatchSize = 1,
            RequestTimeout = TimeSpan.FromSeconds(5)
        });
        client.CaptureLog("held", DebugBundleLogLevel.Error);
        await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var flushes = Enumerable.Range(0, 10_000).Select(_ => client.FlushAsync()).ToArray();
        Assert.InRange(flushes.Count(task => !task.IsCompleted), 0, 64);

        transport.Release();
        await Task.WhenAll(flushes);
    }

    [Fact]
    public async Task InconsistentSenderStateDoesNotEscapeExplicitFlushOrLeakItsWaiter()
    {
        using var client = DebugBundleClient.Create(new DebugBundleOptions
        {
            ProjectToken = "dbundle_proj_test",
            Environment = "test",
            Transport = new FakeTransport(),
            RemoteConfigFetcher = new FakeRemoteConfigFetcher()
        });
        typeof(DebugBundleClient).GetField("_senderRunning", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(client, true);
        typeof(DebugBundleClient).GetField("_senderCompletion", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(client, null);

        await client.FlushAsync();

        Assert.Equal(0, typeof(DebugBundleClient)
            .GetField("_explicitFlushWaiters", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(client));
    }

    [Fact]
    public async Task QueuePressureProducesOneAggregateAfterTheSenderRecovers()
    {
        var transport = new HeldTransport();
        using var client = DebugBundleClient.Create(new DebugBundleOptions
        {
            ProjectToken = "dbundle_proj_test",
            Environment = "test",
            Transport = transport,
            RemoteConfigFetcher = new FakeRemoteConfigFetcher(),
            BatchSize = 1
        });
        client.CaptureLog("first warning", DebugBundleLogLevel.Warning);
        await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        for (var index = 0; index < 600; index++)
            client.CaptureLog($"warning {index}", DebugBundleLogLevel.Warning);
        transport.Release();
        await client.FlushAsync();

        await WaitForSenderAsync(client);
        var reports = transport.Batches.SelectMany(batch => batch)
            .Where(envelope => envelope.EventType == "error_suppressed" &&
                Equals(envelope.Payload.GetValueOrDefault("reason"), "queue_pressure"))
            .ToArray();
        Assert.Single(reports);
        Assert.True(Convert.ToInt64(reports[0].Payload["suppressed_count"]) > 0);
    }

    [Fact]
    public async Task FullLowPriorityQueueRejectsBeforeReadingApplicationProperties()
    {
        var transport = new HeldTransport();
        using var client = DebugBundleClient.Create(new DebugBundleOptions
        {
            ProjectToken = "dbundle_proj_test",
            Environment = "test",
            Transport = transport,
            RemoteConfigFetcher = new FakeRemoteConfigFetcher(),
            BatchSize = 1
        });
        client.CaptureLog("first warning", DebugBundleLogLevel.Warning);
        await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        for (var index = 0; index < 512; index++)
            client.CaptureLog($"warning {index}", DebugBundleLogLevel.Warning);

        var sensitive = new CountedValue();
        client.CaptureLog("dropped warning", DebugBundleLogLevel.Warning,
            new Dictionary<string, object?> { ["expensive"] = sensitive });

        Assert.Equal(0, sensitive.Reads);
        transport.Release();
        await client.FlushAsync();
    }

    private sealed class CountedValue
    {
        private int _reads;
        public int Reads => Volatile.Read(ref _reads);
        public string Value { get { Interlocked.Increment(ref _reads); return "private"; } }
    }

    private sealed class ThrowingMessageException : Exception
    {
        public override string Message => throw new InvalidOperationException("hostile exception getter");
    }

    private sealed class BlockingConfigFetcher : IRemoteConfigFetcher
    {
        public Task<RemoteConfigFetchResult> FetchAsync(RemoteConfigFetchRequest request, CancellationToken cancellationToken = default)
        {
            Thread.Sleep(500);
            return Task.FromResult(new RemoteConfigFetchResult { Config = SdkRemoteConfig.Minimal() });
        }
    }

    private sealed class HeldTransport : IEventTransport
    {
        private readonly TaskCompletionSource<EventTransportResult> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<IReadOnlyList<DebugBundleEventEnvelope>> Batches { get; } = new();
        public int Calls => Volatile.Read(ref _calls);

        public Task<EventTransportResult> SendAsync(EventTransportRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            Batches.Enqueue(request.Events.ToArray());
            Entered.TrySetResult(true);
            return _release.Task;
        }

        public void Release() => _release.TrySetResult(new EventTransportResult { StatusCode = 202 });
    }
}
