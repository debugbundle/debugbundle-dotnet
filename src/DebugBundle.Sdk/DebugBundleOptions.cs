using System.Reflection;
using DebugBundle.Transport;

namespace DebugBundle;

public sealed class DebugBundleOptions
{
    public string? ProjectToken { get; set; }
    public bool Enabled { get; set; } = true;
    public string? Environment { get; set; }
    public string? Service { get; set; }
    public Uri Endpoint { get; set; } = new("https://api.debugbundle.com/v1/events");
    public DebugBundleProjectMode ProjectMode { get; set; } = DebugBundleProjectMode.Connected;
    public string LocalEventsDir { get; set; } = ".debugbundle/local/events";
    public string SpoolDir { get; set; } = ".debugbundle/local/browser-relay-spool";
    public int BatchSize { get; set; } = 25;
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(5);
    public double SampleRate { get; set; } = 1.0;
    public DebugBundleLogLevel LogLevel { get; set; } = DebugBundleLogLevel.Warning;
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan ProbesPollInterval { get; set; } = TimeSpan.FromSeconds(60);
    public int MaxProbeLabels { get; set; } = 50;
    public int MaxProbeEntriesPerLabel { get; set; } = 10;
    public bool ProbeFlushOnError { get; set; } = true;
    public IReadOnlyCollection<string> RedactFields { get; set; } = Array.Empty<string>();
    public IEventTransport? Transport { get; set; }
    public IRemoteConfigFetcher? RemoteConfigFetcher { get; set; }
    public Func<double>? RandomSource { get; set; }

    internal ResolvedDebugBundleOptions Resolve()
    {
        var environment = FirstNonWhiteSpace(
            Environment,
            System.Environment.GetEnvironmentVariable("DEBUGBUNDLE_ENVIRONMENT"),
            System.Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
            System.Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"),
            "development");

        var service = FirstNonWhiteSpace(
            Service,
            System.Environment.GetEnvironmentVariable("DEBUGBUNDLE_SERVICE"),
            Assembly.GetEntryAssembly()?.GetName().Name,
            "dotnet-service");

        var projectToken = FirstNonWhiteSpace(
            ProjectToken,
            System.Environment.GetEnvironmentVariable("DEBUGBUNDLE_TOKEN"));

        return new ResolvedDebugBundleOptions(
            projectToken,
            Enabled,
            environment,
            service,
            Endpoint,
            ProjectMode,
            LocalEventsDir,
            SpoolDir,
            Math.Max(1, BatchSize),
            FlushInterval <= TimeSpan.Zero ? TimeSpan.FromSeconds(5) : FlushInterval,
            ClampSampleRate(SampleRate),
            LogLevel,
            RequestTimeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(5) : RequestTimeout,
            ProbesPollInterval <= TimeSpan.Zero ? TimeSpan.FromSeconds(60) : ProbesPollInterval,
            Math.Max(1, MaxProbeLabels),
            Math.Max(1, MaxProbeEntriesPerLabel),
            ProbeFlushOnError,
            RedactFields,
            Transport,
            RemoteConfigFetcher,
            RandomSource ?? new Random().NextDouble);
    }

    private static string FirstNonWhiteSpace(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value!.Trim();
            }
        }

        return string.Empty;
    }

    private static double ClampSampleRate(double value)
    {
        if (double.IsNaN(value) || value < 0)
        {
            return 0;
        }

        return value > 1 ? 1 : value;
    }
}

internal sealed class ResolvedDebugBundleOptions
{
    public ResolvedDebugBundleOptions(
        string projectToken,
        bool enabled,
        string environment,
        string service,
        Uri endpoint,
        DebugBundleProjectMode projectMode,
        string localEventsDir,
        string spoolDir,
        int batchSize,
        TimeSpan flushInterval,
        double sampleRate,
        DebugBundleLogLevel logLevel,
        TimeSpan requestTimeout,
        TimeSpan probesPollInterval,
        int maxProbeLabels,
        int maxProbeEntriesPerLabel,
        bool probeFlushOnError,
        IReadOnlyCollection<string> redactFields,
        IEventTransport? transport,
        IRemoteConfigFetcher? remoteConfigFetcher,
        Func<double> randomSource)
    {
        ProjectToken = projectToken;
        Enabled = enabled;
        Environment = environment;
        Service = service;
        Endpoint = endpoint;
        ProjectMode = projectMode;
        LocalEventsDir = localEventsDir;
        SpoolDir = spoolDir;
        BatchSize = batchSize;
        FlushInterval = flushInterval;
        SampleRate = sampleRate;
        LogLevel = logLevel;
        RequestTimeout = requestTimeout;
        ProbesPollInterval = probesPollInterval;
        MaxProbeLabels = maxProbeLabels;
        MaxProbeEntriesPerLabel = maxProbeEntriesPerLabel;
        ProbeFlushOnError = probeFlushOnError;
        RedactFields = redactFields;
        Transport = transport;
        RemoteConfigFetcher = remoteConfigFetcher;
        RandomSource = randomSource;
    }

    public string ProjectToken { get; }
    public bool Enabled { get; }
    public string Environment { get; }
    public string Service { get; }
    public Uri Endpoint { get; }
    public DebugBundleProjectMode ProjectMode { get; }
    public string LocalEventsDir { get; }
    public string SpoolDir { get; }
    public int BatchSize { get; }
    public TimeSpan FlushInterval { get; }
    public double SampleRate { get; }
    public DebugBundleLogLevel LogLevel { get; }
    public TimeSpan RequestTimeout { get; }
    public TimeSpan ProbesPollInterval { get; }
    public int MaxProbeLabels { get; }
    public int MaxProbeEntriesPerLabel { get; }
    public bool ProbeFlushOnError { get; }
    public IReadOnlyCollection<string> RedactFields { get; }
    public IEventTransport? Transport { get; }
    public IRemoteConfigFetcher? RemoteConfigFetcher { get; }
    public Func<double> RandomSource { get; }
}
