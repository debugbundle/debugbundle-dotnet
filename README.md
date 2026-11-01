# DebugBundle .NET SDK

DebugBundle for .NET captures backend exceptions, request metadata, structured logs, probe data, and browser relay traffic for ASP.NET Core and worker-style applications.

Version 2.0 changes hook timing and bounds capture ownership. Review [the migration guide](MIGRATION-2.0.md) before upgrading from 1.x. Capture does not invoke custom exception accessors, logging formatters, collection implementations or object getters on application callers. The guide defines deferred weak-reference enrichment and supported primitive/container fallbacks.

This repository follows `spec/sdks/csharp-sdk.md`. The implemented package family contains:

- `DebugBundle.Sdk` core client, static facade, redaction, suppression, probes, HTTP transport, and secure local file transport.
- `DebugBundle.AspNetCore` DI registration, request middleware, shutdown flush, Blazor Server circuit capture, and browser relay endpoint mapping.
- `DebugBundle.Extensions.Logging` `Microsoft.Extensions.Logging` provider with structured state and scope capture.
- `DebugBundle.Serilog`, `DebugBundle.NLog`, and `DebugBundle.Log4Net` optional logger adapters.
- `DebugBundle.Grpc.AspNetCore` ASP.NET Core gRPC server interceptor.
- `DebugBundle.Worker` Generic Host registration, shutdown flush, and background operation wrappers.
- `DebugBundle.Hangfire` server filter for failed job capture.
- `DebugBundle.AzureFunctions.Worker` Azure Functions isolated worker middleware.
- xUnit coverage for universal API, redaction, suppression, transports, ASP.NET Core request capture, relay controls, framework integrations, and logger adapters.

## Evidence in 2.1.0

Version 2.1.0 adds bounded native inner/aggregate graphs and execution
context when `CaptureExceptionEvidence` (default true) and complete reader proof
permit them. Native graph inspection uses base exception getters on the existing
weak-reference worker; unsupported overrides remain unavailable. Existing outer
message/stack and grouping stay authoritative. Observed AppDomain/unobserved-task
failures use two nodes/eight total frames/4KiB combined optional metadata and bypass
hooks, projections and custom sampling. Delivery remains best effort at termination.

Hooks may change any schema-allowed field including ID/type. Complete owned schema,
semantic and privacy validation run again after a valid replacement. Invalid or
throwing hooks preserve the protected original; unsafe/exhausted validation and
unfit accepted primaries are withheld. Optional reduction preserves the latest
accepted primary. Hooks and sampling run once, including retries and held tails.
Native requests count actual serialized bytes and remain within 256KiB. Identity
UTF-8 replies are bounded to 256KiB with one connection/body deadline. Custom HTTP
handlers must honour cancellation. Existing native config/ACKs discover reader
support; file/custom policy polling cannot qualify an unrelated reader path.
`EvidenceCapabilities` is an explicit pin for the entire delivery and reader path.

Use `BeginOperation(new DebugBundleOperationOptions { Kind = "job", Name = "invoice.run" })`
or `RunWithOperationContext[Async]` around known work. `Limit` describes the configured
limit, never remaining time. Scope disposal revokes copied async owners. ASP.NET,
gRPC, Worker, Hangfire and Azure invocation adapters provide their applicable
request/job ownership. Existing `IDebugBundleClient` implementors remain compatible
through optional capability interfaces and extension methods.

Explicit recovery is off by default. Set `CaptureRecoveryObservations = true`,
provide reader proof for `recovery_v1`, create fresh UUID v4 failure and attempt IDs,
and associate the exception with `EvidenceContext.ForFailure(failureRef)`. Inside
the same operation, call `CaptureRecovery(new RecoveryObservation { FailureRef =
failureRef, AttemptId = attemptId, State = "attempt_started" })`. Other explicit
states are `attempt_completed`, `attempt_failed`, `recovery_observed` and
`failure_persisted`; completion/HTTP 200 never implies semantic recovery. An explicit
`EvidenceScope` supports association beyond a disposed operation. Page scope also
requires the matching nonempty session correlation. Recovery names are safe labels,
not identifiers, URLs or arbitrary objects. Metadata and pending observations share
64KiB, 50 windows, five attempts, 20 observations and five minutes; pending offline
receipts prevent allowance renewal.

Browser relays preserve closed evidence DTOs and protected context with server-owned
identity. `X-DebugBundle-Evidence: 1` or `debugbundle_evidence=1` opts into indexed
202 acknowledgements and the complete-path descriptor; legacy mixed-batch 400/string
responses remain compatible. Durable files retain evidence before native forwarding;
forwarded chunks apply current reader proof and count server credentials. One shared
maximum five-second deadline spans chunks and compatible fallback. The optional
`DebugBundleRelayOptions.ForwardTransport` supports explicit custom forwarders;
legacy status-only success leaves a durable spool pending. A held custom forwarder
occupies one bounded worker. Only complete canonical downstream acceptance creates
an exclusive private delivered marker. Review/disable new evidence capture before
adopting it in consumer applications. The published family passed native runtime and installed-artifact release checks.

## Installation

```bash
dotnet add package DebugBundle.AspNetCore
dotnet add package DebugBundle.Extensions.Logging
dotnet add package DebugBundle.Worker
dotnet add package DebugBundle.Sdk
dotnet add package DebugBundle.Grpc.AspNetCore
dotnet add package DebugBundle.Hangfire
dotnet add package DebugBundle.AzureFunctions.Worker
dotnet add package DebugBundle.Serilog
dotnet add package DebugBundle.NLog
dotnet add package DebugBundle.Log4Net
```

## ASP.NET Core Quick Start

```csharp
builder.Services.AddDebugBundle(options =>
{
    options.ProjectToken = builder.Configuration["DEBUGBUNDLE_TOKEN"];
    options.Service = "checkout-api";
    options.Environment = builder.Environment.EnvironmentName;
});
builder.Logging.AddDebugBundle();

var app = builder.Build();
app.UseDebugBundle();
app.MapDebugBundleBrowserRelay("/debugbundle/browser");
// Or middleware style:
// app.UseDebugBundleBrowserRelay("/debugbundle/browser");
```

For Blazor Server circuit exception capture, opt in after `AddDebugBundle()`:

```csharp
builder.Services.AddDebugBundleBlazorServer();
```

## Microsoft.Extensions.Logging

```csharp
builder.Services.AddDebugBundle(options =>
{
    options.ProjectToken = builder.Configuration["DEBUGBUNDLE_TOKEN"];
    options.Service = "checkout-api";
});

builder.Logging.AddDebugBundle(options =>
{
    options.MinimumLevel = LogLevel.Warning;
    options.IncludeScopes = true;
});
```

The logging provider preserves existing providers and captures in-process `log_event` records without reading log files. It includes logger category, level, event ID, rendered message, message template when available, structured state, scopes, `Activity` IDs, and exception summary. SDK-owned categories are skipped by default to avoid recursive capture, and logging callbacks never flush synchronously.

Optional logging adapters are dependency-isolated:

```csharp
Log.Logger = new LoggerConfiguration()
    .WriteTo.DebugBundle(debugBundleClient)
    .CreateLogger();

var nlogTarget = new DebugBundle.NLog.DebugBundleTarget(debugBundleClient);
var log4netAppender = new DebugBundle.Log4Net.DebugBundleAppender(debugBundleClient);
```

## Manual Capture

```csharp
DebugBundle.DebugBundle.Init(new DebugBundleOptions
{
    ProjectToken = Environment.GetEnvironmentVariable("DEBUGBUNDLE_TOKEN"),
    Service = "checkout-worker",
    Environment = "production"
});

DebugBundle.DebugBundle.CaptureException(exception, new Dictionary<string, object?>
{
    ["job_id"] = jobId
});

await DebugBundle.DebugBundle.FlushAsync();
```

## Vanilla Hooks

Vanilla hooks are explicit and idempotent:

```csharp
DebugBundle.DebugBundle.CaptureUnhandledExceptions();
DebugBundle.DebugBundle.CaptureConsoleLogs(includeStandardOutput: false);

DebugBundle.DebugBundle.WithExceptionCapture(() =>
{
    RunJob();
}, new Dictionary<string, object?> { ["job_id"] = jobId });
```

Unhandled exception hooks observe `TaskScheduler.UnobservedTaskException` and `AppDomain.CurrentDomain.UnhandledException` without changing runtime termination or observation behavior. The wrapper helpers capture and rethrow.

## Worker Services

```csharp
builder.Services.AddDebugBundle(options =>
{
    options.ProjectToken = builder.Configuration["DEBUGBUNDLE_TOKEN"];
    options.Service = "billing-worker";
});
builder.Services.AddDebugBundleWorkerCapture();

await debugBundleClient.CaptureOperationAsync("billing.reconcile", async (operationContext, cancellationToken) =>
{
    operationContext.Set("tenant_id", tenantId);
    await ReconcileAsync(cancellationToken);
}, stoppingToken);
```

Operation wrappers capture exceptions with job context and rethrow. Cancellation requested through the provided token is preserved without converting normal shutdown into an error signal.

## gRPC, Hangfire, and Azure Functions

ASP.NET Core gRPC:

```csharp
builder.Services.AddGrpc().AddDebugBundleInterceptor();
```

Hangfire:

```csharp
GlobalJobFilters.Filters.Add(new DebugBundle.Hangfire.DebugBundleHangfireFilter(debugBundleClient));
```

Azure Functions isolated worker:

```csharp
builder.Services.AddSingleton<IDebugBundleClient>(_ => DebugBundleClient.Create(options));
builder.UseDebugBundle();
```

These integrations capture framework metadata, status, exceptions, retry/job/invocation identifiers where available, and sanitized binding or argument summaries. They do not capture protobuf bodies, Blazor component state, Hangfire argument values, Azure binding payload values, form bodies, or SignalR payloads by default.

## Configuration

Configuration precedence is explicit options, `IConfiguration` binding from the `DebugBundle` section, environment variables, then built-in defaults. Capture-policy fields are server-owned and are not accepted from local SDK configuration; connected clients learn capture policy and remote probe directives through `GET /v1/sdk/config`.

| Option | Default | Purpose |
| --- | --- | --- |
| `ProjectToken` | required for connected capture | Server-side write-only project token. |
| `Enabled` | `true` | Global kill switch. |
| `Environment` | `DOTNET_ENVIRONMENT`, `ASPNETCORE_ENVIRONMENT`, then `development` | Service environment name. |
| `Service` | entry assembly name, then `dotnet-service` | Service name used in event envelopes. |
| `Endpoint` | `https://api.debugbundle.com/v1/events` | Connected ingestion endpoint. |
| `ProjectMode` | `Connected` | `Connected` or `LocalOnly`. |
| `LocalEventsDir` | `.debugbundle/local/events` | Local file transport destination. |
| `SpoolDir` | `.debugbundle/local/browser-relay-spool` | Durable browser relay spool destination. |
| `BatchSize` | `25` | Max events per batch. |
| `FlushInterval` | `5s` | Max background flush interval. |
| `SampleRate` | `1.0` | Per-event sampling rate. |
| `LogLevel` | `Warning` | Minimum captured log level. |
| `RequestTimeout` | `5s` | Connected HTTP timeout. |
| `ProbesPollInterval` | `60s` | Backend remote config/probe poll interval when paid-tier remote probes are enabled. |
| `MaxProbeLabels` | `50` | Max distinct probe labels held in memory. |
| `MaxProbeEntriesPerLabel` | `10` | Ring-buffer size per probe label. |
| `ProbeFlushOnError` | `true` | Attach probe buffers to captured exceptions. |

## Capture Policy and Probes

Connected clients fetch `GET /v1/sdk/config` on startup. The response owns capture policy and remote probe directives; local SDK configuration does not accept capture-policy fields.

The SDK enforces:

- log capture modes: `off`, `error`, `warning`, and `info`
- request capture modes: `off`, `failures_only`, `filtered`, and `all`
- immediate request-failure status promotion by preset and project override
- probe capture mode `standalone_when_activated`
- ETag-aware backend polling for paid-tier remote probes
- trigger tokens from `_debug_probe` and `X-DebugBundle-Probe-Trigger`

Always-on probes remain available without remote activation and are flushed with exceptions as versioned `probe_data`.

## Relay Behavior

`MapDebugBundleBrowserRelay()` accepts browser SDK batches at `POST /debugbundle/browser` and answers allowed `OPTIONS` preflight requests. The handler enforces origin validation, `application/json`, a 256 KB body limit, browser event allowlisting, trust-field stripping, per-IP rate limiting, local-only event files, connected durable spool writes, and connected forwarding with server-side credentials only.

## Verification

```bash
make verify
```

`make verify` restores, builds, tests, checks formatting, packs staged NuGet artifacts, and runs a clean-install smoke fixture against the staged packages. The SDK repo is intentionally independent from the core DebugBundle Docker stack. Tests use fake transports and ASP.NET Core test hosts.

## Delivery acknowledgement and retry limits

The built-in HTTP transport requires the canonical ingestion acknowledgement (`accepted`, `rejected`, and `errors`). Empty responses, unrelated JSON, malformed counts, or invalid rejection indices retain the full batch with backoff. Valid acknowledgements remove accepted and terminally rejected events and retry only the indexed retryable rejections; an all-rejected batch does not advance `lastEventAt`.

File transports and explicitly supplied custom/legacy transports retain their documented bodyless success fallback. A custom transport that returns acknowledgement fields must return the complete canonical shape. Retry hints are bounded to five minutes, and failures remain contained within the SDK's existing delivery path.

## Probe checkpoint guidance

Application instrumentation supplies probe labels and bounded data. Deploy a new
checkpoint before activating it. Remote activation affects future matching calls
when policy permits; it does not inject code or recover unretained measurements.
Built-in runtime/error/device collectors keep their documented SDK settings.

Use fixed labels and allowlisted technical states: `media.preview.checkpoint`
(stage/input kind/attempt), `auth.session.checkpoint` (observed session presence
and result), `job.dependency.checkpoint` (named dependency/attempt/result), or
`cache.preview.checkpoint` (hit/miss/coarse age bucket). Omit customer identifiers,
tokens/cookies, cache keys, URLs and raw payloads. HTTP 200 or attempt completion
alone does not establish semantic recovery.

Empty probe data means no matching retained observation for the selected sample
window. It does not confirm activation delivery to every SDK or execution of a
label. Check the deployed SDK/wrapper version, instrumentation, scope, expiry,
policy, sampling and delivery receipts. See the [probe recipes](https://debugbundle.com/docs/probes#bounded-checkpoint-recipes).

## Evidence performance comparison

`make evidence-performance` compares the same 20 native exception/recovery pairs
with evidence disabled/enabled and an empty/full warning queue. It reports caller
time and allocated bytes separately from deferred flush elapsed time and process
allocations, plus retained queue/diagnostic bytes. The deferred stage includes the
full pressure queue and runtime overhead. Setup, native error creation and queue
seeding are excluded. One warm-up and three reported repetitions run with local-only
configuration, an in-memory transport and Docker networking disabled. The harness
checks native causes, original failure retention and existing byte/count ceilings.
Use `DOTNET_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0 SMOKE_TFM=net10.0` for .NET 10.
