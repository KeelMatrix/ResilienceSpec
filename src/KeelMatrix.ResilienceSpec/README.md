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

The `Microsoft.Extensions.Http.Resilience` package is an optional integration/example dependency and is not a runtime
dependency of the ResilienceSpec package. `Microsoft.Extensions.TimeProvider.Testing` is not transitively brought in by
ResilienceSpec; the example uses its public `FakeTimeProvider` API. The clock wrapper uses only public `TimeProvider`
observations and does not load or inspect provider internals.
The verified Microsoft.Extensions.Http.Resilience versions are **9.8.0** and **10.10.0**; other versions are unverified.
See the [repository compatibility contract](https://github.com/KeelMatrix/ResilienceSpec/blob/main/docs/Compatibility.md).

## Logical Call Contract

One `ResilienceScenario` owns exactly one logical operation. Start that operation with
`ResilienceScenario.SendAsync`; this is the only supported root entry point. `scenario.Handler` may be installed in a
manually constructed `HttpClient`, or through `UseResilienceSpecDownstream`, but the request must still be executed
through `scenario.SendAsync`. A direct `HttpClient.SendAsync`, `HttpClient.GetAsync`, factory-client call, or
`HttpMessageInvoker` call that bypasses the runner fails with `ScenarioConsumedException` before consuming a script
step or mutating the report. Genuine retries and timeouts produced inside the configured handler chain inherit the
active logical-call lease. A deliberate request clone must preserve its request options, including the opaque lease
marker; that marker remains sufficient even when execution-context flow is deliberately suppressed. A fresh
unmarked request created inside the ambient handler context is rejected before script/report mutation.
After settlement, cancellation, timeout, or an observation cutoff, the scenario remains permanently consumed and
cannot be reused.

## Quick Example

```csharp
using System.Net;
using KeelMatrix.ResilienceSpec;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Time.Testing;

var underlyingClock = new FakeTimeProvider();
var clock = new ResilienceScenarioClock(underlyingClock, underlyingClock.Advance);
var scenario = new ResilienceScenario(
    HttpFaultScript.Sequence(
        HttpFault.Response(HttpStatusCode.ServiceUnavailable, retryAfter: TimeSpan.FromSeconds(2)),
        HttpFault.Success()),
    clock);

var services = new ServiceCollection();
services.AddSingleton<TimeProvider>(clock.TimeProvider);
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
- `ScriptedHttpMessageHandler` replaces only the terminal network boundary and is owned by its
  `ResilienceScenario`. The verification path answers in memory and never opens a socket, resolves a name, or binds
  a listener. Direct handler/client/invoker sends fail closed with `ScenarioConsumedException`; use
  `ResilienceScenario.SendAsync` for every logical operation.
- `HttpAttemptReport` is an immutable timeline of the attempts that reached that boundary. Records contain only the
  ordinal, method, broad outcome, scripted status or `Retry-After` value, and injected-clock timing.
- Assertions cover exact and maximum attempt counts, method sequence, unsafe-method retries, final status or
  exception, `Retry-After` deltas, retry delays, per-attempt timeout, total timeout, cancellation, and pending runs.
- `UseResilienceSpecDownstream` installs the scripted downstream on an `IHttpClientFactory` client while leaving the
  configured resilience and delegating handlers in place. The core API works without dependency injection.

## Important Limitations

- The package verifies behaviour; it does not add resilience, recommend retry values, or inspect Polly pipeline
  descriptors.
- Timing assertions require a `ResilienceScenarioClock` around a controllable `TimeProvider`, such as the public
  `Microsoft.Extensions.Time.Testing.FakeTimeProvider` API. Keep automatic advancement disabled, register the
  wrapper's `TimeProvider` property in the client pipeline, and pass the clock object to the scenario. The scenario
  invokes the wrapper's verified advance operation, which compares public `GetTimestamp`/`GetElapsedTime` values and
  rejects non-exact net movement. Reentrant movement from a tracked timer callback also fails closed. The public
  `TimeProvider` contract cannot identify an external exact-sum mutation during the same advance, so keep exclusive
  ownership of the underlying provider and treat that sharing pattern as a documented limitation. Without the wrapper,
  timing scenarios fail with `MissingTimeProviderException` instead of falling back to sleeps.
- `Retry-After` is supported in the delta-seconds form only. The HTTP-date form resolves against the wall clock while
  the wait runs on the injected clock, so it cannot be asserted deterministically and is deliberately not exposed.
- Scripted delays use whole-millisecond precision and are capped at `TimeSpan.FromMilliseconds(int.MaxValue)`, matching
  the controlled timer contract. `Retry-After` uses the integer whole-second HTTP wire form, rejects fractional and
  sub-second values, and is bounded by the HTTP delta-seconds limit. Those limits are separate, so a wire-valid value
  above the controlled timer limit can be inspected without being used as a virtual-time wait. Invalid values fail
  during script construction instead of being rounded, dropped, or reported differently.
- Timing observations use `ResilienceScenarioOptions.AdvanceStep` only as a fallback when no provider timer deadline
  is available. When the supported tracking clock exposes a timer deadline, including one exactly at the remaining
  virtual budget, the scenario advances directly to that deadline; a deadline beyond the remaining budget remains an
  honest cutoff. `ResilienceScenarioClock` records provider timers that fire. A continuation that schedules another
  legitimate timer may reach that next deadline, while a completed or disabled one-shot timer does not leave a phantom
  deadline. If a fired timer's continuation neither progresses nor leaves a real tracked deadline within
  `ObservationWindow`, the scenario returns `Pending` at the current virtual time instead of allowing another advance.
  Intermediate virtual delays stay wall-clock cheap because they do not require a watchdog wait before a timer fires.
  The initial client invocation and provider advances run behind the bounded watchdog, so a synchronous callback cannot
  block observation forever; late work remains observed and the logical-call lease stays held through cleanup.
  `ShouldHaveRetryDelay`, `ShouldHaveAttemptDuration`, and `ShouldHaveSettledAtVirtualTime` require exact equality and
  reject an observation affected by fallback sampling; `AdvanceStep` is a sampling interval, not a tolerance.
  `ShouldRespectRetryAfter` is still a minimum assertion, so a longer exact wait is valid, but sampled or incomplete
  timing evidence is rejected.
- When the virtual budget or pending observation expires, `SendAsync` returns `Pending` and the report marks
  `IsObservationCutoff`; `ShouldHaveSettledAtVirtualTime` accepts only genuine request settlement. An attempt ended by
  observation cleanup has incomplete timing evidence, so `ShouldHaveAttemptDuration` rejects it instead of treating
  cleanup's virtual duration as a configured timeout; earlier genuinely completed attempts remain independently
  assertable. Cleanup is bounded by `ResilienceScenarioOptions.CleanupTimeout`; late completion is observed and late
  responses are disposed, but arbitrary user code that ignores cancellation cannot be forcibly terminated. The
  single-consumer lease remains held until late cleanup completes. Cleanup releases retained resources, but the scenario
  remains consumed permanently and a second logical call always fails with `ScenarioConsumedException`.
- One `ResilienceScenario` serves exactly one logical call for its lifetime. Direct sends that bypass the runner and
  sequential reuse both fail with `ScenarioConsumedException`; `ConcurrentScriptUseException` is reserved for genuine
  overlapping attempts inside the active call. Create one scenario per logical call.
- `ShouldRespectRetryAfter` verifies the advertised value as a minimum wait; use `ShouldHaveRetryDelay` for an exact
  configured delay. It requires exact timing evidence for the interval it evaluates. A response that advertises
  `Retry-After` without a following retry produces a specific diagnostic.
- The recorded attempt timeline keeps at most `HttpAttemptReport.MaximumRecordedAttempts` attempts, so a client whose
  retry predicate covers harness failures cannot grow attempt state inside the test process. Such a run reports
  `HttpAttemptReport.IsOverflowed` and fails attempt-state assertions with `AttemptStateOverflowException` instead of
  judging a partial timeline; result-level assertions remain evaluable. When overflowed, `LastAttempt` is the last
  recorded attempt rather than necessarily the final served attempt. A sufficiently long runaway retry loop may still
  return `Pending` when its observation window ends, with the exact served count and overflow state preserved.
- The package targets `net8.0`. Hosted validation runs the package gate on Windows, Linux, and macOS, then runs explicit
  integration checks against both `9.8.0` and `10.10.0`. Windows and Linux external-command validation claim provable
  whole-tree containment through a Job Object or cgroup v2/child-subreaper process-tree proof. macOS uses
  session/process-group containment plus descendant inspection; its result exposes that boundary and documents the
  limitation that a process deliberately creating a new session can escape. Detectable containment, inspection,
  termination, and cleanup errors fail closed rather than claiming containment.
- The package gate packs twice, normalizes ZIP entry timestamps to `1980-01-01 00:00:00` ZIP-local time, stores entries
  without compression, emits LF-only UTF-8 package text payloads, compares the `.nupkg` and `.snupkg` SHA256 values, and
  inspects the normalized artifacts before the clean consumer restore. This makes the
  package artifact identity reproducible for the fixed commit and SDK selected by `global.json`.

## Supported Integration Range

The verified Microsoft.Extensions.Http.Resilience versions are **9.8.0** and **10.10.0**; other versions are unverified.
Both endpoints use `Microsoft.Extensions.TimeProvider.Testing` `10.10.0`; other testing-package versions are
unverified. The implementation-neutral script, report, and assertion core does not reference
`Microsoft.Extensions.Http.Resilience` or Polly. The shipping package intentionally references
`Microsoft.Extensions.Http` for its factory adapter and `KeelMatrix.Telemetry`; Polly and
`Microsoft.Extensions.Http.Resilience` remain outside its runtime dependency graph. See the
[repository compatibility contract](https://github.com/KeelMatrix/ResilienceSpec/blob/main/docs/Compatibility.md).

## Telemetry

ResilienceSpec requests the shared activation and heartbeat only after a scenario has settled, an executed qualifying
failure has been observed, and a resilience assertion is evaluated after settlement. It passes no scenario or request
data to the shared client's no-argument methods. The shared client owns payload, duplicate suppression, cadence, opt-out,
identity, delivery, and failure behavior; see [PRIVACY.md](https://github.com/KeelMatrix/ResilienceSpec/blob/main/PRIVACY.md)
and its links to the maintained shared policy.

When enabled, telemetry may make an outbound network request. The in-memory verification path itself needs no network;
KeelMatrix validation sets `KEELMATRIX_NO_TELEMETRY=1`, and consumers can use the shared opt-out documented by KeelMatrix.Telemetry.

## Documentation

- [Repository README](https://github.com/KeelMatrix/ResilienceSpec/blob/main/README.md)
- [Privacy](https://github.com/KeelMatrix/ResilienceSpec/blob/main/PRIVACY.md)
- [Security policy](https://github.com/KeelMatrix/ResilienceSpec/blob/main/SECURITY.md)
- [Changelog](https://github.com/KeelMatrix/ResilienceSpec/blob/main/CHANGELOG.md)

## License

MIT.
