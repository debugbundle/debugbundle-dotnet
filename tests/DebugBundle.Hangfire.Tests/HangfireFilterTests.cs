using DebugBundle.Hangfire;
using Hangfire;
using Hangfire.Common;
using Hangfire.Server;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;

namespace DebugBundle.Hangfire.Tests;

public sealed class HangfireFilterTests
{
    [Fact]
    public void OnPerformed_Captures_Failed_Job_Metadata_Without_Argument_Values()
    {
        var client = new FakeClient();
        var filter = new DebugBundleHangfireFilter(client);
        var job = Job.FromExpression(() => SampleJobs.SampleJob("secret-value", 3));
        var backgroundJob = new BackgroundJob("job-123", job, DateTime.UtcNow);
        var perform = new PerformContext(new StubJobStorage(), new StubStorageConnection(), backgroundJob, new StubCancellationToken());
        var performing = new PerformingContext(perform);
        performing.Items["Queue"] = "critical";
        var exception = new InvalidOperationException("failed");
        var performed = new PerformedContext(performing, null, false, exception);

        filter.OnPerformed(performed);

        var captured = Assert.Single(client.Exceptions);
        Assert.Same(exception, captured.Exception);
        Assert.Equal("hangfire", captured.Context!["framework"]);
        Assert.Equal("job-123", captured.Context["job_id"]);
        Assert.Equal(nameof(SampleJobs.SampleJob), captured.Context["job_method"]);
        Assert.Equal("critical", captured.Context["queue"]);
        var arguments = Assert.IsAssignableFrom<IReadOnlyList<Dictionary<string, object?>>>(captured.Context["argument_summaries"]);
        Assert.Equal(2, arguments.Count);
        Assert.Equal("string", arguments[0]["kind"]);
        Assert.DoesNotContain("secret-value", captured.Context.Values.Select(value => value?.ToString()));
    }

    [Fact]
    public void OnPerformed_Ignores_Successful_Jobs()
    {
        var client = new FakeClient();
        var filter = new DebugBundleHangfireFilter(client);
        var job = Job.FromExpression(() => SampleJobs.SampleJob("value", 1));
        var backgroundJob = new BackgroundJob("job-123", job, DateTime.UtcNow);
        var perform = new PerformContext(new StubJobStorage(), new StubStorageConnection(), backgroundJob, new StubCancellationToken());
        var performing = new PerformingContext(perform);
        var performed = new PerformedContext(performing, null, false, null);

        filter.OnPerformed(performed);

        Assert.Empty(client.Exceptions);
    }

    public static class SampleJobs
    {
        public static void SampleJob(string customerId, int attempt)
        {
            _ = customerId;
            _ = attempt;
        }
    }

    private sealed class StubStorageConnection : JobStorageConnection
    {
        public override IWriteOnlyTransaction CreateWriteTransaction() => throw new NotSupportedException();
        public override IDisposable AcquireDistributedLock(string resource, TimeSpan timeout) => throw new NotSupportedException();
        public override string CreateExpiredJob(Job job, IDictionary<string, string> parameters, DateTime createdAt, TimeSpan expireIn) => throw new NotSupportedException();
        public override IFetchedJob FetchNextJob(string[] queues, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override void SetJobParameter(string id, string name, string value) { }
        public override string? GetJobParameter(string id, string name) => null;
        public override JobData? GetJobData(string jobId) => null;
        public override StateData? GetStateData(string jobId) => null;
        public override void AnnounceServer(string serverId, ServerContext context) { }
        public override void RemoveServer(string serverId) { }
        public override void Heartbeat(string serverId) { }
        public override int RemoveTimedOutServers(TimeSpan timeOut) => 0;
        public override HashSet<string> GetAllItemsFromSet(string key) => new();
        public override string? GetFirstByLowestScoreFromSet(string key, double fromScore, double toScore) => null;
        public override void SetRangeInHash(string key, IEnumerable<KeyValuePair<string, string>> keyValuePairs) { }
        public override Dictionary<string, string> GetAllEntriesFromHash(string key) => new();
    }

    private sealed class StubJobStorage : JobStorage
    {
        public override IStorageConnection GetConnection() => new StubStorageConnection();
        public override IMonitoringApi GetMonitoringApi() => throw new NotSupportedException();
    }

    private sealed class StubCancellationToken : IJobCancellationToken
    {
        public CancellationToken ShutdownToken => CancellationToken.None;
        public void ThrowIfCancellationRequested()
        {
        }
    }
}
