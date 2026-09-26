# KeelMatrix.ResilienceSpec

KeelMatrix.ResilienceSpec verifies what your configured `HttpClient` actually does when the downstream fails. Keep
the real handler chain, replace only the terminal network boundary with a deterministic script, and assert attempt
count, unsafe-method behaviour, final outcome, and — on an injected clock — retry timing.

The package does **not** make a system resilient and it does not configure resilience. It verifies the observable
behaviour of a client that is already configured, so a configuration mistake fails in a test instead of in
production. ResilienceSpec verifies the assembled client's observable HTTP and resilience behaviour without
introspecting or replacing the resilience implementation. The verification path needs no listener, socket, DNS
lookup, container, or hosted service: the terminal handler never opens a socket, resolves a name, or binds a
listener. The optional telemetry described under [Telemetry](#telemetry) is separate, best-effort, opt-out network
behaviour that KeelMatrix validation disables.

## Install

```text
dotnet add package KeelMatrix.ResilienceSpec
dotnet add package Microsoft.Extensions.Http.Resilience --version 10.10.0
dotnet add package Microsoft.Extensions.TimeProvider.Testing --version 10.10.0
```

The second command installs the optional `Microsoft.Extensions.Http.Resilience` integration/example dependency; it is
not a runtime dependency of the ResilienceSpec package. The third package is not transitively brought in by
ResilienceSpec, but timing scenarios require the explicitly referenced supported `Microsoft.Extensions.TimeProvider.Testing`
`10.10.0` package at test runtime. Other testing-package versions are unverified and are not admitted for timing
evidence. The supported resilience integration range is `9.8.0` up to, but not including, `11.0.0`.

## Logical Call Contract

One `ResilienceScenario` owns exactly one logical operation. Start that operation with
`ResilienceScenario.SendAsync`; this is the only supported root entry point. `scenario.Handler` may be installed in a
manually constructed `HttpClient`, or through `UseResilienceSpecDownstream`, but the request must still be executed
through `scenario.SendAsync`. A direct `HttpClient.SendAsync`, `HttpClient.GetAsync`, factory-client call, or
`HttpMessageInvoker` call that bypasses the runner fails with `ScenarioConsumedException` before consuming a script
step or mutating the report. Genuine retries and timeouts produced inside the configured handler chain inherit the
active logical-call lease. After settlement, cancellation, timeout, or an observation cutoff, the scenario remains
permanently consumed and cannot be reused.

## Quick Start

Install the scripted downstream on the client under test, keep the resilience configuration you ship, and run the
request through the scenario so that retries happen on the injected clock.

```csharp
using System.Net;
using KeelMatrix.ResilienceSpec;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Time.Testing;
using Polly;

var underlyingClock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
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
    .AddStandardResilienceHandler()
    .Configure(options =>
    {
        options.Retry.MaxRetryAttempts = 1;
        options.Retry.Delay = TimeSpan.FromSeconds(2);
        options.Retry.BackoffType = DelayBackoffType.Constant;
        options.Retry.UseJitter = false;
    });

using var provider = services.BuildServiceProvider();
var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("orders");

using var request = new HttpRequestMessage(HttpMethod.Get, "https://orders.invalid/orders/42");
using var result = await scenario.SendAsync(client, request);

result.ShouldHaveStatus(HttpStatusCode.OK);
scenario.Report
    .ShouldHaveAttempts(2)
    .ShouldHaveMethodSequence(HttpMethod.Get, HttpMethod.Get)
    .ShouldRespectRetryAfter();
```

Nothing in this example binds a port, resolves a host name, or reaches the network while the verification path runs.
The reserved `.invalid` host is answered in memory by the scripted terminal handler. The optional telemetry described
under [Telemetry](#telemetry) is the only part of a default run that can reach the network, and
`KEELMATRIX_NO_TELEMETRY=1` disables it.

## What You Can Assert

| Concept | API |
| --- | --- |
| Scripted downstream steps | `HttpFault.Response`, `HttpFault.Success`, `HttpFault.NetworkError`, `HttpFault.Timeout`, `HttpFault.Delay`, composed with `HttpFaultScript.Sequence`, `HttpFaultScript.Repeat`, `HttpFaultScript.Always` |
| Terminal handler | `ScriptedHttpMessageHandler` |
| Scenario and injected clock | `ResilienceScenario`, `ResilienceScenarioOptions`, `ResilienceScenario.SendAsync` |
| Immutable attempt report | `HttpAttemptReport`, `HttpAttempt`, `HttpAttemptOutcome` |
| Final caller-visible outcome | `ResilienceResult`, `ResilienceResultKind` |
| Assertions | `ShouldHaveAttempts`, `ShouldHaveAtMostAttempts`, `ShouldHaveMethodSequence`, `ShouldNotHaveRetried`, `ShouldRespectRetryAfter`, `ShouldHaveRetryDelay`, `ShouldHaveAttemptDuration`, `ShouldHaveSettledAtVirtualTime`, `ShouldHaveStatus`, `ShouldHaveKind`, `ShouldBePending`, `ShouldHaveException<T>` |
| HttpClientFactory adapter | `ResilienceSpecHttpClientBuilderExtensions.UseResilienceSpecDownstream` |

An assertion failure reports the expectation, the observation, and the observed timeline:

```text
Expected exactly 2 attempt(s) at the scripted downstream, but the downstream served 1 attempt(s).

Timeline:
#1 GET -> response 503 (retry-after 2 s) | started +0 ms | lasted 0 ms
```

## GET Retries And The 503 -> 200 Scenario

```csharp
var scenario = new ResilienceScenario(
    HttpFaultScript.Sequence(
        HttpFault.Response(HttpStatusCode.ServiceUnavailable),
        HttpFault.Success()),
    clock);

// ... configure the client as in the quick start ...

using var request = new HttpRequestMessage(HttpMethod.Get, "https://orders.invalid/orders/42");
using var result = await scenario.SendAsync(client, request);

result.ShouldHaveStatus(HttpStatusCode.OK);
scenario.Report.ShouldHaveAttempts(2).ShouldHaveRetryDelay(TimeSpan.FromSeconds(2));
```

## POST Must Not Be Retried

Microsoft's standard resilience handler retries all HTTP methods by default, including unsafe ones. When your
configuration disables that, the invariant is directly assertable:

```csharp
services.AddHttpClient("payments")
    .UseResilienceSpecDownstream(scenario)
    .AddStandardResilienceHandler()
    .Configure(options =>
    {
        options.Retry.MaxRetryAttempts = 3;
        options.Retry.Delay = TimeSpan.FromSeconds(1);
        options.Retry.BackoffType = DelayBackoffType.Constant;
        options.Retry.UseJitter = false;
        options.Retry.DisableForUnsafeHttpMethods();
    });

// ... run the POST through the scenario ...

using var result = await scenario.SendAsync(client, request);

result.ShouldHaveStatus(HttpStatusCode.ServiceUnavailable);
scenario.Report.ShouldHaveAttempts(1).ShouldNotHaveRetried(HttpMethod.Post);
```

`ShouldNotHaveRetried` counts the attempts of that method at the scripted downstream, so it fails with the observed
timeline when the configured client repeats an unsafe request.

## Retry-After (Delta Seconds)

```csharp
var scenario = new ResilienceScenario(
    HttpFaultScript.Sequence(
        HttpFault.Response(HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.FromSeconds(5)),
        HttpFault.Success()),
    clock);

// ... run the request ...

scenario.Report.ShouldRespectRetryAfter().ShouldHaveRetryDelay(TimeSpan.FromSeconds(5));
```

The delta form is deterministic because the wait runs on the injected clock. The HTTP-date form is deliberately not
supported; see [Timing Limitations](#timing-limitations).
Scripted `Retry-After` values use whole-millisecond precision and must not exceed the maximum duration supported by the
controlled timer (`TimeSpan.FromMilliseconds(int.MaxValue)`).

## Exceptions And Cancellation

```csharp
var scenario = new ResilienceScenario(
    HttpFaultScript.Sequence(HttpFault.NetworkError(), HttpFault.Success()),
    clock);

using var result = await scenario.SendAsync(client, request);

// The chain handled the network-like failure and retried it.
result.ShouldHaveStatus(HttpStatusCode.OK);
scenario.Report.ShouldHaveAttempts(2);
```

```csharp
var scenario = new ResilienceScenario(
    HttpFaultScript.Sequence(HttpFault.Timeout()),
    clock);

using var caller = new CancellationTokenSource();
var run = scenario.SendAsync(client, request, caller.Token);
await caller.CancelAsync();
using var result = await run;

result.ShouldHaveKind(ResilienceResultKind.Canceled);
```

`HttpFault.Timeout()` never answers, so an attempt can only end through a timeout strategy or through caller
cancellation. The result distinguishes them: `ResilienceResultKind.Canceled` when your token was cancelled,
`ResilienceResultKind.Timeout` when the chain abandoned the attempt without you asking, `DownstreamError` when an
exception reached the caller, `ScriptExhausted` when the script ran out of steps, and `ConcurrentUse` for the
low-level overlapping-attempt diagnostic. A second top-level `ResilienceScenario.SendAsync` call throws
`ScenarioConsumedException` before it can consume the script. `ConcurrentScriptUseException` is reserved for two
attempts overlapping inside one active logical call.

## Deterministic Timing

Timing assertions are available only when a scenario is created with a `ResilienceScenarioClock` around an exact
`Microsoft.Extensions.Time.Testing.FakeTimeProvider` instance:

```csharp
var underlyingClock = new FakeTimeProvider();
var clock = new ResilienceScenarioClock(underlyingClock, underlyingClock.Advance);
var scenario = new ResilienceScenario(script, clock);
```

`ResilienceScenarioClock` must wrap the controllable provider used by the resilience pipeline. Its advance delegate
must advance that provider once by exactly the requested duration; no-op, partial, extra, offset, different-provider,
or direct out-of-band advances fail as harness configuration errors instead of producing timing evidence. The
provider's `AutoAdvanceAmount` must be zero when the wrapper is created and throughout the run, so reading the clock
cannot move virtual time. Register the wrapper's
`TimeProvider` property with `services.AddSingleton<TimeProvider>(clock.TimeProvider)` and pass the clock object to
the scenario. The scenario invokes the wrapper's verified advance operation itself. The wrapper records which provider timers fire during each advance, so the scenario can distinguish an
ordinary intermediate delay from a timer whose continuation has not reached the scripted downstream. The adapter
fails configuration with a `MissingTimeProviderException` when the scenario clock is missing or a different provider
instance is registered. Admission loads `Microsoft.Extensions.TimeProvider.Testing.dll` version `10.10.0.0` from the
exact directory beside the package assembly, checks the expected Microsoft strong-name public-key token, and compares
the provider type with `FakeTimeProvider` from that explicitly loaded assembly. `TimeProvider.System`, other testing
package versions, consumer-authored derived or delegating
providers, same-name assemblies loaded from another path, and resolver-hook substitutions are rejected. This
provenance check does not attest a consumer-replaced file at that exact dependency path; unsupported providers cannot
produce a timing-eligible report.

While a request is pending, `ResilienceScenario.SendAsync` advances to the next tracked provider timer when one is
available and otherwise uses `AdvanceStep` as a fallback. It waits for scripted-downstream progress only after a timer
fires, using `ObservationWindow` as a bounded watchdog. A continuation that schedules another legitimate timer is
allowed to reach that next deadline; a completed or disabled one-shot timer does not create a phantom deadline, and a
genuinely stalled continuation still returns `Pending` without another advance. This is the fail-closed quiescence
boundary and keeps ordinary virtual delays wall-clock cheap.
`ShouldHaveRetryDelay`, `ShouldHaveAttemptDuration`, and `ShouldHaveSettledAtVirtualTime` require exact equality on the
injected clock. `HttpAttemptReport.ObservationStep` reports the fallback sampling interval; it is never an implicit
tolerance. If the specific observation was available only through fallback sampling, the exact assertion fails with an
actionable diagnostic even when the sampled number happens to equal the expectation. Nothing is measured with the wall
clock and there are no elapsed-time tolerances. `ShouldRespectRetryAfter` remains a minimum assertion: a longer exact
wait is valid when the observed wait is at least the advertised delta, but sampled or incomplete timing evidence is
rejected.

Assertions that ship with the timing subset:

| Assertion | What it proves |
| --- | --- |
| `ShouldRespectRetryAfter()` | Every scripted `Retry-After` delta was honoured as the minimum wait before the next attempt with exact timing evidence |
| `ShouldHaveRetryDelay(expected)` | Every inter-attempt delay matches the configured backoff |
| `ShouldHaveAttemptDuration(ordinal, expected)` | One attempt lasted the configured per-attempt timeout |
| `ShouldHaveSettledAtVirtualTime(expected)` | The request settled at the configured total timeout |

Timing assertions throw `MissingTimeProviderException` when the scenario has no controllable clock. A request that is
still pending when observation stops is not a settled request: its report marks `IsObservationCutoff`, and
`ShouldHaveSettledAtVirtualTime` rejects it. The package never falls back to sleeps or tolerances.

The "clock not advanced" control is part of the same contract. Set `AdvanceClock` to `false` to observe a pending
request without moving time:

```csharp
var options = new ResilienceScenarioOptions
{
    AdvanceClock = false,
    PendingObservation = TimeSpan.FromMilliseconds(200),
};

var scenario = new ResilienceScenario(
    HttpFaultScript.Sequence(HttpFault.Delay(TimeSpan.FromSeconds(2), HttpFault.Success())),
    clock,
    options);

// ... run the request ...

result.ShouldBePending();
scenario.Report.ShouldHaveAttempts(1);
```

When observation ends because `VirtualBudget` is exhausted or `AdvanceClock` is disabled, the result remains
`Pending` and the report marks `IsObservationCutoff`. That cutoff is not settlement evidence, so
`ShouldHaveSettledAtVirtualTime` rejects it. An attempt ended by observation cleanup has incomplete timing evidence;
`ShouldHaveAttemptDuration` rejects it rather than treating cleanup's virtual duration as a configured timeout.
Earlier attempts that genuinely completed remain independently assertable. Cancellation cleanup is bounded by
`ResilienceScenarioOptions.CleanupTimeout`; late faults are observed and late responses are disposed, but arbitrary
user code that ignores cancellation cannot be forcibly terminated. If cleanup outlives that bound, the scenario retains
its single-consumer lease until the late request and cancellation callbacks finish. Cleanup releases retained
resources, but the scenario remains consumed permanently; every second logical call fails with
`ScenarioConsumedException`.

### Timing Limitations

- **`Retry-After` HTTP-date is not supported.** The date delta is resolved against the wall clock while the resulting
  wait runs on the injected clock, so the two disagree whenever the test clock and the wall clock differ. Scripted
  responses therefore carry the delta-seconds form only, and `HttpFault.Response` has no HTTP-date overload.
- **Timing assertions need an injected clock.** Without one, attempt, method, outcome, and unsafe-method assertions
  still work, and timing assertions fail with an actionable configuration error.
- **Exact timing observations require supported timer deadlines.** `ResilienceScenarioClock` lets the scenario advance
  directly to the next provider timer, so supported retry/delay observations are not rounded up by the fallback
  `AdvanceStep`. When no timer deadline is available, `AdvanceStep` is only a sampling interval; an exact assertion
  over an observation affected by that sampling fails closed instead of treating the interval as tolerance.
- **Timing scenarios require the exact supported runtime type.** Wrap an exact
  `Microsoft.Extensions.Time.Testing.FakeTimeProvider` from `Microsoft.Extensions.TimeProvider.Testing` `10.10.0`,
  leave `AutoAdvanceAmount` at zero, wrap it with `ResilienceScenarioClock`, and register `clock.TimeProvider`.
  Admission uses the type loaded from the version- and strong-name-checked testing assembly at the dependency path
  beside the package assembly, so other package versions, raw, derived, delegating, same-name cross-assembly, and
  resolver-hook-spoofed consumer providers are rejected instead of claiming a deterministic timing result. The check
  does not attest a file a consumer replaces at that exact path.
- **`Retry-After` is asserted for delta responses only.** A response without `Retry-After` is governed by the
  configured backoff, which `ShouldHaveRetryDelay` verifies.
- **Script duration precision is explicit.** `HttpFault.Delay` and `HttpFault.Response` retry-after deltas accept
  non-negative whole milliseconds only, with positive delays and deltas capped at `TimeSpan.FromMilliseconds(int.MaxValue)`.
  Fractional-millisecond and out-of-range values are rejected while the script is constructed; they are never silently
  rounded or reported as a different duration. `AdvanceStep` and `VirtualBudget` are injected-clock durations, while
  `ObservationWindow`, `PendingObservation`, and `CleanupTimeout` are wall-clock watchdogs with the same whole-
  millisecond timer range.

## Recording And Privacy

Attempt records are intentionally minimal. `HttpAttempt` contains only the attempt ordinal, the HTTP method, the broad
outcome, the scripted status code, the scripted `Retry-After` value, and injected-clock timing. The package never
records request URIs, query strings, header values, cookies, authorization values, request or response bodies, or
exception messages, and the local timeline it prints on failure contains no request data.

Scripted responses carry an empty body and no headers other than the scripted `Retry-After` value. A script is bounded
to `HttpFaultScript.MaximumSteps` steps, and the recorded attempt timeline keeps at most
`HttpAttemptReport.MaximumRecordedAttempts` attempts, which is that same value. A client that somehow makes more
attempts than that — for example by retrying a harness failure such as `ScriptExhaustedException` — still gets the
served `AttemptCount`, a report whose `IsOverflowed` is `true`, and a timeline that ends with an explicit truncation
line. Assertions over attempt state then fail with `AttemptStateOverflowException` instead of judging a partial
timeline, so the served attempt count is never silently truncated. A `ResilienceScenario` represents exactly one
logical call for its lifetime. Only `ResilienceScenario.SendAsync` may start it; direct handler, manual-client,
factory-client, and invoker sends fail with `ScenarioConsumedException` before consuming a step or mutating the
report. A second root call also fails with `ScenarioConsumedException`. `ConcurrentScriptUseException` is reserved
for overlapping attempts inside the active call. Create one scenario per logical call.

The recording and verification path described here needs no listener, socket, DNS lookup, container, or hosted
service. Only the optional telemetry described under [Telemetry](#telemetry) can resolve a name or open a socket, and
it is disabled by `KEELMATRIX_NO_TELEMETRY=1`. Result-level assertions remain evaluable on an overflowed report
because the caller-visible outcome is exact; attempt-state assertions refuse the incomplete timeline. When
`IsOverflowed` is `true`, `LastAttempt` is the last recorded attempt, not necessarily the final attempt served by the
downstream. A very long runaway retry loop can still return a pending result when the observation window ends; the
report retains the exact served count and explicit overflow state, so adjust the client's retry budget or observation
configuration.

## Integration With Microsoft.Extensions.Http.Resilience

The scripted downstream is installed as the primary (innermost) handler, so the resilience strategies, delegating
handlers, and ordering of the client under test all stay in place. `UseResilienceSpecDownstream` returns the
`IHttpClientBuilder`, which means it chains before `AddStandardResilienceHandler()`:

```csharp
services.AddHttpClient("orders")
    .UseResilienceSpecDownstream(scenario)
    .AddStandardResilienceHandler();
```

Supported and tested range: `Microsoft.Extensions.Http.Resilience` **9.8.0 and newer, below 11.0**. The repository's
integration suite runs against the lowest tested release (9.8.0) and the current release (10.10.0) with
`-p:ResilienceVersion=`. Both endpoints use `Microsoft.Extensions.TimeProvider.Testing` `10.10.0`; other versions of
that testing package are unverified and rejected by timing-provider admission. The implementation-neutral script,
report, and assertion core does not reference `Microsoft.Extensions.Http.Resilience` or Polly. The shipping package
intentionally references `Microsoft.Extensions.Http` for its factory adapter and `KeelMatrix.Telemetry`; Polly and
`Microsoft.Extensions.Http.Resilience` remain outside its runtime dependency graph.

## Configuration Inspection Versus Observable Behaviour

Inspection tools answer "what pipeline did I construct?" by reading options and pipeline descriptors.
KeelMatrix.ResilienceSpec answers "what did this assembled client do?" by counting the attempts that really reached
the network boundary, in order, with their methods and outcomes. Configuration intent and observable behaviour can
differ — for example when a retry predicate, a disabling API, or a timeout interacts with the method of the request —
so this package deliberately asserts the assembled behaviour instead of restating configuration.

## Platforms And Target Frameworks

- Target framework: `net8.0`.
- The package and its tests use portable .NET APIs. Hosted validation runs the package gate on Windows, Linux, and
  macOS, then runs explicit integration checks against both `9.8.0` and `10.10.0`.
- Only `net8.0` is exercised. The macOS evidence comes from a hosted, virtualized `macos-latest` runner, not physical
  macOS hardware. The package gate requires the exact `.NET SDK 10.0.401` pinned by `global.json` with SDK roll-forward
  disabled. It packs twice, normalizes ZIP entry timestamps to `1980-01-01 00:00:00` ZIP-local time, stores entries
  without compression, emits LF-only UTF-8 package text payloads, compares both archive SHA256 values, prints a sorted
  entry-level manifest and its canonical identity SHA256, and inspects the normalized artifacts before the clean
  consumer restore. The manifest includes the generated nuspec entries, so the recorded identity is reproducible in an
  independent environment using the pinned toolchain.

## Telemetry

The package uses the shared `KeelMatrix.Telemetry` activation and weekly heartbeat contract. An activation is
requested only when a scripted scenario actually reached at least one injected failure **and** at least one resilience
assertion was evaluated; constructing a script, handler, or scenario never activates telemetry. Telemetry is
best-effort, never a reliability dependency, cannot break the host, and can be disabled by setting
`KEELMATRIX_NO_TELEMETRY=1`. See [PRIVACY.md](PRIVACY.md) for the full contract.

Telemetry is the one part of the package that is network behaviour rather than an in-memory verification: when it is
enabled, an activation is posted over HTTPS to the shared KeelMatrix telemetry endpoint, which resolves a name and
opens a socket. KeelMatrix validation sets `KEELMATRIX_NO_TELEMETRY=1`, which is why the repository's own zero-socket
evidence covers the verification path and not the optional telemetry transport.

## Troubleshooting

| Symptom | Cause and next step |
| --- | --- |
| `MissingTimeProviderException` when creating a client | The scenario has a controllable clock but the container has no matching tracking provider. Register `clock.TimeProvider` with `services.AddSingleton<TimeProvider>(clock.TimeProvider)`, or set `RequireRegisteredTimeProvider` to `false` when the pipeline time source is configured another way. |
| `MissingTimeProviderException` from a timing scenario | Use an exact `Microsoft.Extensions.Time.Testing.FakeTimeProvider` from `Microsoft.Extensions.TimeProvider.Testing` `10.10.0`, keep `AutoAdvanceAmount` at zero, wrap it with `new ResilienceScenarioClock(underlyingClock, underlyingClock.Advance)`, and pass that clock object to the scenario. Other testing-package versions and derived, delegating, same-name cross-assembly, or resolver-hook-spoofed providers are rejected. |
| `ScenarioConsumedException` | A direct handler/client/invoker send bypassed `ResilienceScenario.SendAsync`, or the one logical call was already consumed. The request failed before script/report mutation. |
| `ConcurrentScriptUseException` | Two attempts overlapped inside one active logical call. Create one scenario per logical call. |
| `ScriptExhaustedException` | The client under test made more attempts than the script describes. Extend the script with `HttpFaultScript.Sequence` or `HttpFaultScript.Always`. |
| `AttemptStateOverflowException` | The client under test served more attempts than `HttpAttemptReport.MaximumRecordedAttempts`, so the recorded timeline is incomplete and cannot judge attempt state. Lower the client's configured maximum attempts; a script cannot describe more attempts than `HttpFaultScript.MaximumSteps` steps. |
| A timing assertion says exact evidence is unavailable | The observation required fallback sampling because no supported provider timer deadline was available. `AdvanceStep` is a sampling interval, not a tolerance. Make the operation expose a timer on `clock.TimeProvider`, or use a non-timing assertion. |
| A clock-configuration error reports unexpected movement | Keep `AutoAdvanceAmount` at zero, advance only through `clock.Advance`, and ensure the constructor delegate advances the wrapped provider once with the unchanged requested duration. |
| A request never settles | The script contains a step that waits for the clock or never answers. Check that the injected clock is registered and that `AdvanceClock` is enabled, or assert `ShouldBePending()`. |

## Documentation

- [docs/DEV.md](docs/DEV.md) documents the local validation path and package gates.
- [CHANGELOG.md](CHANGELOG.md) lists released and unreleased changes.
- [SECURITY.md](SECURITY.md) explains how to report a vulnerability privately.
- [PRIVACY.md](PRIVACY.md) describes telemetry and data handling.
- [CONTRIBUTING.md](CONTRIBUTING.md) documents how to contribute changes.

## License

MIT. See [LICENSE](LICENSE).
