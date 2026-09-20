# KeelMatrix.ResilienceSpec

KeelMatrix.ResilienceSpec verifies what your configured `HttpClient` actually does when the downstream fails. Keep the
real handler chain, replace only the terminal network boundary with a deterministic script, and assert attempt count,
unsafe-method behaviour, final outcome, and — on an injected clock — retry timing.

The package does not make a system resilient and it does not configure resilience. It verifies an already configured
client. The verification path needs no listener, socket, DNS lookup, container, or hosted service; the optional
telemetry described under [Telemetry](#telemetry) is separate, best-effort, opt-out network behaviour that KeelMatrix
validation disables.

## Install

```text
dotnet add package KeelMatrix.ResilienceSpec
dotnet add package Microsoft.Extensions.Http.Resilience --version 10.10.0
dotnet add package Microsoft.Extensions.TimeProvider.Testing --version 10.10.0
```

The additional commands install the fake-clock and Microsoft standard-handler packages used by this example. They are
not runtime dependencies of `KeelMatrix.ResilienceSpec`; use matching supported versions when testing another
`Microsoft.Extensions.Http.Resilience` release.

## Quick Example

```csharp
using System.Net;
using KeelMatrix.ResilienceSpec;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Time.Testing;

var clock = new FakeTimeProvider();
var scenario = new ResilienceScenario(
    HttpFaultScript.Sequence(
        HttpFault.Response(HttpStatusCode.ServiceUnavailable, retryAfter: TimeSpan.FromSeconds(2)),
        HttpFault.Success()),
    clock,
    clock.Advance);

var services = new ServiceCollection();
services.AddSingleton<TimeProvider>(clock);
services.AddHttpClient("orders")
    .UseResilienceSpecDownstream(scenario)
    .AddStandardResilienceHandler();

using var provider = services.BuildServiceProvider();
var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("orders");
using var request = new HttpRequestMessage(HttpMethod.Get, "https://orders.invalid/orders/42");
using var result = await scenario.SendAsync(client, request);

result.ShouldHaveStatus(HttpStatusCode.OK);
scenario.Report.ShouldHaveAttempts(2).ShouldRespectRetryAfter();
```

## What Ships

- `HttpFault` and `HttpFaultScript` describe a deterministic sequence of responses, network-like failures, gated
  timeouts, and clock-based delays.
- `ScriptedHttpMessageHandler` replaces only the terminal network boundary. The verification path answers in memory
  and never opens a socket, resolves a name, or binds a listener.
- `HttpAttemptReport` is an immutable timeline of the attempts that reached that boundary. Records contain only the
  ordinal, method, broad outcome, scripted status or `Retry-After` value, and injected-clock timing.
- Assertions cover exact and maximum attempt counts, method sequence, unsafe-method retries, final status or
  exception, `Retry-After` deltas, retry delays, per-attempt timeout, total timeout, cancellation, and pending runs.
- `UseResilienceSpecDownstream` installs the scripted downstream on an `IHttpClientFactory` client while leaving the
  configured resilience and delegating handlers in place. The core API works without dependency injection.

## Important Limitations

- The package verifies behaviour; it does not add resilience, recommend retry values, or inspect Polly pipeline
  descriptors.
- Timing assertions require a scenario created with a controllable `TimeProvider` and the operation that advances it.
  Without one, timing assertions fail with `MissingTimeProviderException` instead of falling back to sleeps.
- `Retry-After` is supported in the delta-seconds form only. The HTTP-date form resolves against the wall clock while
  the wait runs on the injected clock, so it cannot be asserted deterministically and is deliberately not exposed.
- Timing observations wait for scripted-downstream progress after each injected-clock advance and are sampled at
  `ResilienceScenarioOptions.AdvanceStep` granularity. An adapter that can hold a continuation after a timer fires
  must set `ResilienceScenarioOptions.WaitForPipelineProgress`; an incomplete callback returns `Pending` at the
  current virtual time instead of allowing another advance.
- When the virtual budget or pending observation expires, `SendAsync` returns `Pending` and the report marks
  `IsObservationCutoff`; `ShouldHaveSettledAtVirtualTime` accepts only genuine request settlement. Cleanup is bounded
  by `ResilienceScenarioOptions.CleanupTimeout`; late completion is observed and late responses are disposed, but
  arbitrary user code that ignores cancellation cannot be forcibly terminated.
- One script serves one logical call. Concurrent use fails with `ConcurrentScriptUseException` unless
  `ScriptConcurrency.AllowConcurrent` is requested.
- `ShouldRespectRetryAfter` verifies the advertised value as a minimum wait; use `ShouldHaveRetryDelay` for an exact
  configured delay. A response that advertises `Retry-After` without a following retry produces a specific diagnostic.
- The recorded attempt timeline keeps at most `HttpAttemptReport.MaximumRecordedAttempts` attempts, so a client whose
  retry predicate covers harness failures cannot grow attempt state inside the test process. Such a run reports
  `HttpAttemptReport.IsOverflowed` and fails attempt-state assertions with `AttemptStateOverflowException` instead of
  judging a partial timeline; result-level assertions remain evaluable. When overflowed, `LastAttempt` is the last
  recorded attempt rather than necessarily the final served attempt. A sufficiently long runaway retry loop may still
  return `Pending` when its observation window ends, with the exact served count and overflow state preserved.
- The package targets `net8.0`. Hosted validation runs Full validation on Windows and macOS against `10.10.0`; Linux runs
  the portable core/integration script. Every runner then runs explicit integration checks against both `9.8.0` and
  `10.10.0`. The macOS evidence comes from a hosted, virtualized `macos-latest` runner, not physical macOS hardware.
- Linux package-stage parity is not established by this workflow.

## Supported Integration Range

The integration tests cover `Microsoft.Extensions.Http.Resilience` **9.8.0 and newer, below 11.0**, and are run
against the lowest tested release and the current release. The core package does not reference
`Microsoft.Extensions.Http.Resilience` or Polly.

## Telemetry

An activation is requested only after a scripted scenario reached at least one injected failure and evaluated at
least one resilience assertion. Telemetry is best-effort, cannot break the host, and can be disabled with
`KEELMATRIX_NO_TELEMETRY=1`. The package never transmits request data, client names, URLs, headers, bodies, or
exception messages.

Telemetry is the one part of the package that is network behaviour rather than an in-memory verification: when it is
enabled, an activation is posted over HTTPS to the shared KeelMatrix telemetry endpoint, which resolves a name and
opens a socket. KeelMatrix validation sets `KEELMATRIX_NO_TELEMETRY=1`, and a consumer that needs a fully offline
test process sets it too.

## Documentation

- [Repository README](https://github.com/KeelMatrix/ResilienceSpec/blob/main/README.md)
- [Privacy](https://github.com/KeelMatrix/ResilienceSpec/blob/main/PRIVACY.md)
- [Security policy](https://github.com/KeelMatrix/ResilienceSpec/blob/main/SECURITY.md)
- [Changelog](https://github.com/KeelMatrix/ResilienceSpec/blob/main/CHANGELOG.md)

## License

MIT.
