# Changelog

## [Unreleased]

## [2.1.1] - 2026-11-06

- Read duplicate-capture metadata from the protected SDK snapshot instead of calling application-defined dictionary accessors.
- Final audit: only admitted captures create one-use cross-path receipts; repeated native records remain separate. Deferred hooks or sampling retain uncertain reports rather than letting a rejected candidate suppress another.

- Emit one exception for Serilog/NLog/log4net native exception records, preserving log context. Route Microsoft.Extensions.Logging error exceptions through native cause/aggregate capture while retaining deferred formatting and caller safety. Deduplicate native logger/framework reports within owned operations.
- Public client interfaces and event schemas remain compatible. Adoption requires upgrading the SDK.

## [2.1.0] - 2026-11-01

- Reuse immutable mandatory privacy patterns and compile bounded custom fields once
  per protection operation, avoiding repeated allocation without sharing caller policy.
- Add complete bounded owned hook validation from the packaged canonical schema,
  semantic graph/resource/scope checks and receiving JSON byte vectors. Valid ID/type
  replacements remain supported; unavailable/exhausted validation is withheld.
- Revalidate final privacy and reduce optional metadata without restoring pre-hook
  primary content. Emit canonical UTC Z timestamps, preserve Activity metadata in
  context/canonical correlation and retain tokenless local capture.
- Add native cause/aggregate evidence, revocable request/job operations, explicit
  opt-in recovery and bounded pending receipt ownership. Keep base client implementors compatible.
- Use callback-free bounded observed-fatal capture without changing termination semantics.
- Fit final native request bytes and preserve accepted hook identities across retries/tails;
  discover/revoke reader proof from existing bounded native config and acknowledgements.
- Preserve closed browser evidence/context and server identity through relays, with opted-in
  indexed responses, whole-forward deadlines, complete-ACK private markers and explicit custom forwarders.
- Split the large client into existing public partial-class boundaries. Final runtime,
  artifact, performance and whole-family release checks passed.

## [2.0.1] - 2026-09-26

### Fixed

- Bound numeric HTTP retry hints before duration conversion to prevent overflow; preserve safe fallback for invalid hints.

- Require a valid canonical acknowledgement from built-in HTTP delivery; retain the full batch and back off for missing or malformed responses. Preserve bodyless file and explicit custom transport compatibility.
- Cap transport retry hints at five minutes before scheduling retries, including custom transport results.

## [2.0.0] - 2026-09-25

### Changed

- Reject logs and requests against effective local/remote policy before building and privacy-scanning events. Filtered and sampled-out events no longer invoke `BeforeSend`.
- Fetch initial remote configuration off the constructor thread with a restrictive initial policy. Run `BeforeSend`, local file and HTTP transport through one coalesced sender instead of from capture callbacks.
- Bound pending captured events to 512 and combined pending/in-flight capture ownership to 1,000 events/8 MiB, including hook replacements and suppression aggregates. Reserve incident capacity, prioritize exceptions and failed requests over lower-severity logs and ordinary requests, emit queue-pressure aggregates, and retry queued events after backoff without requiring another application capture. Valid hook changes to event IDs retain independent resource accounting.
- Move custom exception accessors and reference-state logging formatters onto the existing sender using weak handles; preserve ordinary exception summaries and primitive Microsoft logging templates. Custom value-state formatters and arbitrary object/collection accessors use documented safe fallbacks.
- Cap duplicate-suppression state at 2,048 fingerprints.
- Bound concurrent explicit `FlushAsync` waiters to 64 so a held sender cannot create an unbounded number of timeout timers.
- See [MIGRATION-2.0.md](MIGRATION-2.0.md) before upgrading from 1.x.

## [1.5.0] - 2026-09-21

### Security

- Enforce mandatory bounded telemetry protection before context and probe retention, after `BeforeSend`, in the buffer and transport, and in the ASP.NET browser relay. Custom keys add to the baseline.

## [1.4.0] - 2026-09-12

- Use Source Link bundled with the .NET 8+ SDK instead of overriding it with the vulnerable 8.0.0 build package (CVE-2026-62900).

### Changed

- License first-party SDK code under Apache-2.0 and ship consistent package licensing metadata and license text.

## [1.3.0] - 2026-07-28

### Added

- Added the universal `BeforeSend` event hook and canonical object wrapping for scalar/list probe values.
- Added real-HTTP clean-package delivery verification with canonical ingestion acknowledgement assertions.
- Added a canonical merged Cobertura gate that enforces at least 80% line coverage for every production source file, with behavior coverage for the static facade, middleware and registration extensions, worker lifecycle, gRPC streaming paths, relay validation, and logging adapters.

### Fixed

- Reconcile connected ingestion acknowledgements per event, retaining only retryable rejections and withholding `LastEventAt` when no event was accepted.
- Consolidated framework logging adapters on the shared static client facade while preserving their public registration and fail-open behavior.

## [1.2.0] - 2026-07-17

### Added

- Corrected the semantic release line for browser-relay analytics support. Relay handlers accept credential-free `analytics_event` envelopes while preserving only the required analytics correlation fields and stripping browser-supplied credentials.

## [1.1.2] - 2026-07-17

### Added

- Added browser-relay support for `analytics_event` envelopes, preserving only the analytics correlation fields needed for aggregation while continuing to strip browser-supplied credentials.

## [1.1.1] - 2026-06-19

### Fixed

- Normalized canonical event-envelope emission so custom app context now stays in envelope `context`, request events avoid legacy payload extras, and installed projects stop tripping malformed ingestion rejects after upgrade.
- Corrected NuGet package license metadata to `initial open-source` so published package metadata matches the repository license.

## [1.1.0] - 2026-06-08

### Added

- Added path-scoped immediate client-error incident promotion support in the remote capture-policy handling so explicitly configured `4xx` routes can emit standalone `request_event` incident signals without widening the status globally.

### Changed

- Unpromoted client-error request telemetry now remains context-only under repeated traffic, while `5xx` handling and explicitly promoted client-error behavior are preserved.
