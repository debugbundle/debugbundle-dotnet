# Changelog

## [Unreleased]

## [1.1.1] - 2026-06-19

### Fixed

- Normalized canonical event-envelope emission so custom app context now stays in envelope `context`, request events avoid legacy payload extras, and installed projects stop tripping malformed ingestion rejects after upgrade.
- Corrected NuGet package license metadata to `AGPL-3.0-only` so published package metadata matches the repository license.

## [1.1.0] - 2026-06-08

### Added

- Added path-scoped immediate client-error incident promotion support in the remote capture-policy handling so explicitly configured `4xx` routes can emit standalone `request_event` incident signals without widening the status globally.

### Changed

- Unpromoted client-error request telemetry now remains context-only under repeated traffic, while `5xx` handling and explicitly promoted client-error behavior are preserved.
