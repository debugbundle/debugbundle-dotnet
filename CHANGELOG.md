# Changelog

## [Unreleased]

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
