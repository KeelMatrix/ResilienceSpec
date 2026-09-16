# Changelog

All notable changes to KeelMatrix.ResilienceSpec are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/), and this project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.0] - Planned

### Added

- A scripted in-memory downstream (`HttpFault`, `HttpFaultScript`, `ScriptedHttpMessageHandler`) that replaces only the
  terminal network boundary of a configured `HttpClient`, so no listener, socket, DNS lookup, or hosted service is
  required.
- An immutable attempt report and assertion helpers for exact and maximum attempt counts, method sequence,
  "this unsafe request was not retried", final response or exception, and cancellation outcomes, with failures that
  print the observed timeline.
- Deterministic timing assertions for retry delays, `Retry-After` deltas, per-attempt timeouts, and total-request
  timeouts, driven by an injected `TimeProvider`; timing assertions are unavailable, and fail with
  `MissingTimeProviderException`, when no controllable clock is supplied.
- An `IHttpClientFactory` adapter that composes the scripted downstream with the client's existing resilience and
  delegating handlers, while the core API stays usable without dependency injection.
- Attempt records and diagnostics that exclude URIs, query strings, headers, cookies, authorization values, bodies,
  and exception messages, with bounded scripts, a bounded attempt timeline that reports `IsOverflowed` and fails
  attempt-state assertions with `AttemptStateOverflowException` instead of truncating silently, and single-consumer
  semantics by default.
- Best-effort activation telemetry through `KeelMatrix.Telemetry` only after a scenario reached an injected failure and
  evaluated a resilience assertion, with `KEELMATRIX_NO_TELEMETRY=1` opt-out.
