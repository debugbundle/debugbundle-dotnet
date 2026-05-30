# DebugBundle .NET SDK

DebugBundle for .NET captures backend exceptions, request metadata, structured logs, probe data, and browser relay traffic for ASP.NET Core and worker-style applications.

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
