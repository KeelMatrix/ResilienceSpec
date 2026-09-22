# Changelog

All notable changes to KeelMatrix.ResilienceSpec are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/), and this project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed

- Observation-cutoff cleanup no longer supplies a virtual duration that can satisfy a per-attempt timeout assertion;
  genuinely completed earlier attempts remain independently assertable.
- Native `HttpClient.Timeout` cancellation is classified as `Timeout`, while caller and unrelated cancellations remain
  distinct.
- Direct scripted-handler use now rejects untracked time providers instead of presenting wall-clock timing as
  deterministic.
- `ResilienceScenarioClock` admits only the `FakeTimeProvider` type loaded from the strong-name-token-checked
  `Microsoft.Extensions.TimeProvider.Testing.dll` at the dependency path beside the package assembly. `TimeProvider.System`,
  consumer-authored derived/delegating providers, same-name assemblies from another path, and resolver-hook substitutions
  are rejected; a consumer-replaced file at that exact path is outside this provenance check.
- Standard validation runs the core and integration test projects sequentially, avoiding cross-project scheduler
  contention while preserving the bounded fail-closed timing watchdog.
- Package validation pins the complete `.NET SDK 10.0.401` toolchain, produces byte-identical `.nupkg` and `.snupkg`
  artifacts for a fixed commit, and records a sorted entry-level SHA256 manifest that includes generated nuspec entries
  before consumer smoke.
- Corrected the root README examples to pass the tracked `clock.TimeProvider`.

## [0.1.0] - Planned

### Added

- A scripted in-memory downstream (`HttpFault`, `HttpFaultScript`, `ScriptedHttpMessageHandler`) that replaces only the
  terminal network boundary of a configured `HttpClient`, so the verification path requires no listener, socket, DNS
  lookup, or hosted service.
- An immutable attempt report and assertion helpers for exact and maximum attempt counts, method sequence,
  "this unsafe request was not retried", final response or exception, and cancellation outcomes, with failures that
  print the observed timeline; pending observation cutoffs remain distinct from genuine request settlement.
- Deterministic timing assertions for retry delays, `Retry-After` deltas, per-attempt timeouts, and total-request
  timeouts, driven by the `FakeTimeProvider` type from the strong-name-token-checked testing assembly at the dependency
  path beside the package assembly. Timing assertions are unavailable, and fail with `MissingTimeProviderException`,
  when no supported fake clock is supplied. Consumer-authored derived, delegating, same-name cross-assembly, and
  resolver-hook-spoofed providers cannot obtain timing eligibility; a consumer-replaced file at that exact path is outside
  this provenance check.
- A fail-closed virtual-time progress contract based on `ResilienceScenarioClock`: when a provider timer fires but its
  continuation does not reach the scripted downstream within the observation window, the scenario returns an honest
  pending observation instead of advancing past work that has not progressed.
- Bounded cancellation cleanup that retains the single-consumer lease through late completion, plus atomic publication
  of response outcome, status, and `Retry-After` metadata in live attempt snapshots.
- An `IHttpClientFactory` adapter that composes the scripted downstream with the client's existing resilience and
  delegating handlers, while the core API stays usable without dependency injection.
- Attempt records and diagnostics that exclude URIs, query strings, headers, cookies, authorization values, bodies,
  and exception messages, with bounded scripts, a bounded attempt timeline that reports `IsOverflowed` and fails
  attempt-state assertions with `AttemptStateOverflowException` instead of truncating silently, and single-consumer
  semantics by default.
- Best-effort activation telemetry through `KeelMatrix.Telemetry` only after a scenario reached an injected failure and
  evaluated a resilience assertion, with `KEELMATRIX_NO_TELEMETRY=1` opt-out.
