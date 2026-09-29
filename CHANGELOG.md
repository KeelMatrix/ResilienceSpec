# Changelog

All notable changes to KeelMatrix.ResilienceSpec are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/), and this project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed

- The bounded external-command runner now establishes Windows Job Object or Unix cgroup v2 containment before
  execution, rejects every containment-error state, contains detached Unix descendants when cgroup v2 is available,
  and fails closed when that proof is unavailable. Release workflow validation and publication gates use the same
  runner, with an executable workflow guard rejecting newly introduced direct external gates.
- Full validation now bounds every repository command and test step, and the dependency audit terminates its advisory
  query child process with captured diagnostics on timeout; hosted validation gives the full-validation and direct
  integration steps their own deadlines.
- The verified Microsoft.Extensions.Http.Resilience versions are **9.8.0** and **10.10.0**. Other versions are unverified.
- Linux validation now runs the same formatting, Release build, package-consumer, and required direct/transitive
  dependency gates as the other supported runners; the tag workflow also checks exact remote-main provenance and both
  verified integration endpoints before publication.
- The documentation hygiene guard scans every tracked authored text/source file with strict UTF-8, ASCII, BOM-aware and
  bounded BOM-less UTF 16/UTF 32 decoding, including NUL-rich text and unsupported paths/extensions. Binary exclusion is
  limited to declared PNG paths with authoritative PNG validation, including `tEXt`, `zTXt`, and compressed and uncompressed
  `iTXt` metadata scanning. Unlisted, malformed, undecidable, unsupported-format, and clean-text PNG impostors fail closed;
  opaque or compressed binary payloads that are not text or PNG textual metadata remain outside this bounded scan guarantee.
- Logical-call ownership now requires a positive opaque request association; fresh requests created by flowed handlers
  cannot consume another script step, while deliberate request clones remain supported when they preserve source options,
  including across deliberately suppressed execution-context flow.
- Timer schedule publication is reentrancy-aware, and initial sends/provider advances are bounded by observation
  watchdogs so immediate changes, one-shot timers, and synchronous callbacks cannot create phantom deadlines or hang
  observation. Each prepared scenario also starts elapsed-time and exact-evidence tracking at logical-call admission.
- Telemetry policy: An activation is requested only after a settled scenario has either observed an injected failure
  published by an executed attempt, an executed injected timeout classified as a timeout by the settled client or
  strategy, or a positively recognized native HttpClient.Timeout outcome with at least one executed attempt, and an
  assertion is evaluated at or after settlement. ResponseFault is true only for an HTTP response with status 400 or
  higher published by an executed attempt. ExceptionFault is true only for a network failure published by an
  executed attempt, an executed injected timeout classified as a timeout by the settled client or strategy, or a
  positively recognized native HttpClient.Timeout outcome whose cancellation token is the same token passed to an
  executed scripted attempt and is canceled. Plain caller cancellation, arbitrary upstream timeout exceptions
  (including native-shaped exceptions without that token evidence), unused planned script steps, and observation
  cleanup do not set ExceptionFault. Failure categories accumulate across executed attempts: any case with a
  published response or network category produces one signal containing the accumulated categories, while a case
  with no category produces zero sink signals.
- `ShouldRespectRetryAfter` now fails closed when the evaluated interval used sampled or incomplete timing evidence,
  while retaining the minimum-wait rule for exact equal or longer waits.
- Virtual-time observation now follows legitimate timer-to-timer continuations and correctly retires zero-period,
  infinite-period, disabled, changed, and disposed one-shot timers without inventing due-now callbacks.
- Scripted response statuses and duration inputs are validated before execution; fractional-millisecond or
  out-of-range timer durations are rejected, and response completion is published only after response construction
  succeeds.
- Release validation now parses exact invariant ISO calendar dates and shares the canonical stable-tag grammar with the
  release workflow.
- Release validation now fails closed when a finalized tag leaves substantive content under `[Unreleased]`, and the
  repository validation path checks reachable commit history for non-product provenance or authorship metadata and
  non-conforming authorship.
- Exact retry-delay, attempt-duration, and settlement assertions now require equality on exact injected-clock evidence;
  fallback sampling is reported as unavailable exact evidence instead of acting as an implicit tolerance.
- `ResilienceScenarioClock` now uses only public `TimeProvider` observations, rejects non-exact movement and reentrant
  timer-callback mutation, and documents that public APIs cannot identify an external exact-sum mutation during one
  advance.
- Observation-cutoff cleanup no longer supplies a virtual duration that can satisfy a per-attempt timeout assertion;
  genuinely completed earlier attempts remain independently assertable.
- Native `HttpClient.Timeout` cancellation is classified as `Timeout`, while caller and unrelated cancellations remain
  distinct.
- Direct scripted-handler use now rejects untracked time providers instead of presenting wall-clock timing as
  deterministic.
- Timing scenarios accept the `ResilienceScenarioClock` object and invoke its verified advance operation, which compares
  public timestamp values and records timer progress without provider-internal reflection. `TimeProvider.System` remains
  unsupported; callers must retain exclusive ownership of the controllable provider.
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
  timeouts, driven by public `TimeProvider` timestamp and timer APIs. Timing assertions are unavailable, and fail with
  `MissingTimeProviderException`, when no controllable tracking clock is supplied. Non-exact movement and reentrant
  timer-callback mutation fail closed; public APIs cannot identify an external exact-sum mutation during one advance,
  so callers must keep exclusive ownership of the underlying provider.
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
- Best-effort activation telemetry through `KeelMatrix.Telemetry`. Telemetry policy: An activation is requested only
  after a settled scenario has either observed an injected failure published by an executed attempt, an executed
  injected timeout classified as a timeout by the settled client or strategy, or a positively recognized native
  HttpClient.Timeout outcome with at least one executed attempt, and an assertion is evaluated at or after settlement.
  ResponseFault is true only for an HTTP response with status 400 or higher published by an executed attempt.
  ExceptionFault is true only for a network failure published by an executed attempt, an executed injected timeout
  classified as a timeout by the settled client or strategy, or a positively recognized native HttpClient.Timeout
  outcome whose cancellation token is the same token passed to an executed scripted attempt and is canceled. Plain
  caller cancellation, arbitrary upstream timeout exceptions (including native-shaped exceptions without that token
  evidence), unused planned script steps, and observation cleanup do not set ExceptionFault. Failure categories
  accumulate across executed attempts: any case with a published response or network category produces one signal
  containing the accumulated categories, while a case with no category produces zero sink signals. The opt-out remains
  `KEELMATRIX_NO_TELEMETRY=1`.
