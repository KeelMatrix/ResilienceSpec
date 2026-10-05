# Changelog

All notable changes to KeelMatrix.ResilienceSpec are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/), and this project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.0] - 2026-10-05

### Added

- Provides an in-memory scripted terminal handler for deterministic HTTP responses, network-like failures, timeouts,
  and delays, so tests can exercise the configured `HttpClient` handler chain without opening sockets, resolving DNS,
  or binding listeners.
- Records an immutable, bounded attempt timeline and provides assertions for attempt counts, method sequences, unsafe
  request retries, final outcomes, cancellation, and pending observations.
- Supports exact retry-delay, `Retry-After` delta-seconds, and timeout assertions through a `ResilienceScenarioClock`
  around a controllable `TimeProvider`; timing assertions reject incomplete or non-exact evidence.
- Provides an `IHttpClientFactory` adapter that preserves the client's configured resilience and delegating handlers,
  while keeping the core API usable without dependency injection.
- The verified Microsoft.Extensions.Http.Resilience versions are **9.8.0** and **10.10.0**; other versions are unverified.
- Keeps attempt records and diagnostics free of request URIs, headers, authorization values, bodies, and exception
  messages, while optional shared telemetry passes no scenario or request data and is requested only after an executed
  qualifying failure in a settled scenario with a post-settlement resilience assertion.
