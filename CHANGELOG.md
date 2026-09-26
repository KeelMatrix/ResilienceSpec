# Changelog

All notable changes to KeelMatrix.ResilienceSpec are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/), and this project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed

- `ShouldRespectRetryAfter` now fails closed when the evaluated interval used sampled or incomplete timing evidence,
  while retaining the minimum-wait rule for exact equal or longer waits.
- Virtual-time observation now follows legitimate timer-to-timer continuations and correctly retires zero-period,
  infinite-period, disabled, changed, and disposed one-shot timers without inventing due-now callbacks.
- Scripted response statuses and duration inputs are validated before execution; fractional-millisecond or
  out-of-range timer durations are rejected, and response completion is published only after response construction
  succeeds.
- Release validation now parses exact invariant ISO calendar dates and shares the canonical stable-tag grammar with the
  release workflow.
- Exact retry-delay, attempt-duration, and settlement assertions now require equality on exact injected-clock evidence;
  fallback sampling is reported as unavailable exact evidence instead of acting as an implicit tolerance.
- `ResilienceScenarioClock` now rejects testing-provider versions other than `10.10.0`, non-zero automatic advancement,
  unexpected provider movement, and advance delegates that do not move the admitted provider by exactly the requested
  duration.
- Observation-cutoff cleanup no longer supplies a virtual duration that can satisfy a per-attempt timeout assertion;
  genuinely completed earlier attempts remain independently assertable.
- Native `HttpClient.Timeout` cancellation is classified as `Timeout`, while caller and unrelated cancellations remain
  distinct.
- Direct scripted-handler use now rejects untracked time providers instead of presenting wall-clock timing as
  deterministic.
- Timing scenarios now accept the `ResilienceScenarioClock` object and invoke its verified advance operation, so an
  independently supplied provider or advance delegate cannot create timing evidence.
- `ResilienceScenarioClock` admits only the `FakeTimeProvider` type loaded from the strong-name-token-checked
  `Microsoft.Extensions.TimeProvider.Testing.dll` at the dependency path beside the package assembly. `TimeProvider.System`,
  consumer-authored derived/delegating providers, same-name assemblies from another path, and resolver-hook substitutions
  are rejected; a consumer-replaced file at that exact path is outside this provenance check.
- Standard validation runs the core and integration test projects sequentially, avoiding cross-project scheduler
  contention while preserving the bounded fail-closed timing watchdog.
- Package validation pins the complete `.NET SDK 10.0.401` toolchain, produces byte-identical `.nupkg` and `.snupkg`
  artifacts for a fixed commit, and records a sorted entry-level SHA256 manifest that includes generated nuspec entries
  before consumer smoke.
- Corrected the root README examples to bind timing scenarios to the tracked `ResilienceScenarioClock`.
- A `ResilienceScenario` is now permanently single-use: cleanup releases retained resources without making a consumed
  scenario reusable, and the unsafe public concurrent-script option has been removed.

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
