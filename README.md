# KeelMatrix.ResilienceSpec

KeelMatrix.ResilienceSpec verifies what your configured `HttpClient` actually does when the downstream fails. Keep
the real handler chain, replace only the terminal network boundary with a deterministic script, and assert attempt
count, unsafe-method behaviour, final outcome, and — on an injected clock — retry timing.

The package does **not** make a system resilient and it does not configure resilience. It verifies the observable
behaviour of a client that is already configured, so a configuration mistake fails in a test instead of in
production. Only `System.Net.Http` behaviour is under test, and the verification path needs no listener, socket, DNS
lookup, container, or hosted service: the terminal handler never opens a socket, resolves a name, or binds a
listener. The optional telemetry described under [Telemetry](#telemetry) is separate, best-effort, opt-out network
behaviour that KeelMatrix validation disables.

## Install

```text
dotnet add package KeelMatrix.ResilienceSpec
dotnet add package Microsoft.Extensions.Http.Resilience --version 10.10.0
dotnet add package Microsoft.Extensions.TimeProvider.Testing --version 10.10.0
```

The second and third commands install the optional example prerequisites. The core package does not bring in the
Microsoft resilience integration or fake-clock test package; choose matching supported versions when testing another
`Microsoft.Extensions.Http.Resilience` release.

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

var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

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
    clock,
    clock.Advance);

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
    clock,
    clock.Advance);

// ... run the request ...

scenario.Report.ShouldRespectRetryAfter().ShouldHaveRetryDelay(TimeSpan.FromSeconds(5));
```

The delta form is deterministic because the wait runs on the injected clock. The HTTP-date form is deliberately not
supported; see [Timing Limitations](#timing-limitations).

## Exceptions And Cancellation

```csharp
var scenario = new ResilienceScenario(
    HttpFaultScript.Sequence(HttpFault.NetworkError(), HttpFault.Success()),
    clock,
    clock.Advance);

using var result = await scenario.SendAsync(client, request);

// The chain handled the network-like failure and retried it.
result.ShouldHaveStatus(HttpStatusCode.OK);
scenario.Report.ShouldHaveAttempts(2);
```

```csharp
var scenario = new ResilienceScenario(
    HttpFaultScript.Sequence(HttpFault.Timeout()),
    clock,
    clock.Advance);

using var caller = new CancellationTokenSource();
var run = scenario.SendAsync(client, request, caller.Token);
await caller.CancelAsync();
using var result = await run;

result.ShouldHaveKind(ResilienceResultKind.Canceled);
```

`HttpFault.Timeout()` never answers, so an attempt can only end through a timeout strategy or through caller
cancellation. The result distinguishes them: `ResilienceResultKind.Canceled` when your token was cancelled,
`ResilienceResultKind.Timeout` when the chain abandoned the attempt without you asking, `DownstreamError` when an
exception reached the caller, `ScriptExhausted` when the script ran out of steps, and `ConcurrentUse` when one script
was consumed by two in-flight requests.

## Deterministic Timing

Timing assertions are available only when a scenario is created with a controllable `TimeProvider` and the operation
that advances it:

```csharp
var clock = new FakeTimeProvider();
var scenario = new ResilienceScenario(script, clock, clock.Advance);
```

The same clock instance must drive the resilience pipeline, which is what `services.AddSingleton<TimeProvider>(clock)`
does for `Microsoft.Extensions.Http.Resilience`. The adapter resolves the registered service and fails configuration
with a `MissingTimeProviderException` when the scenario clock is missing or a different `TimeProvider` instance is
registered, so a timing assertion can never silently observe a pipeline that runs on another clock.

While a request is pending, `ResilienceScenario.SendAsync` advances the injected clock in `AdvanceStep` increments and
waits for the scripted downstream's progress signal before considering another advance. Timing assertions compare the
observed injected-clock value with the expected value and allow the declared sampling granularity reported by
`HttpAttemptReport.ObservationStep`; nothing is measured with the wall clock and there are no elapsed-time tolerances.

Assertions that ship with the timing subset:

| Assertion | What it proves |
| --- | --- |
| `ShouldRespectRetryAfter()` | Every scripted `Retry-After` delta was honoured as the minimum wait before the next attempt |
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
    clock.Advance,
    options);

// ... run the request ...

result.ShouldBePending();
scenario.Report.ShouldHaveAttempts(1);
```

When observation ends because `VirtualBudget` is exhausted or `AdvanceClock` is disabled, the result remains
`Pending` and the report marks `IsObservationCutoff`. That cutoff is not settlement evidence, so
`ShouldHaveSettledAtVirtualTime` rejects it. Cancellation cleanup is bounded by
`ResilienceScenarioOptions.CleanupTimeout`; late faults are observed and late responses are disposed, but arbitrary
user code that ignores cancellation cannot be forcibly terminated.

### Timing Limitations

- **`Retry-After` HTTP-date is not supported.** The date delta is resolved against the wall clock while the resulting
  wait runs on the injected clock, so the two disagree whenever the test clock and the wall clock differ. Scripted
  responses therefore carry the delta-seconds form only, and `HttpFault.Response` has no HTTP-date overload.
- **Timing assertions need an injected clock.** Without one, attempt, method, outcome, and unsafe-method assertions
  still work, and timing assertions fail with an actionable configuration error.
- **Timing observations are sampled.** The clock advances in `AdvanceStep` increments, so the observed value can lag
  the exact release instant by at most one step. Lower `AdvanceStep` for finer observation; each advance costs one
  observation window of wall-clock time.
- **`Retry-After` is asserted for delta responses only.** A response without `Retry-After` is governed by the
  configured backoff, which `ShouldHaveRetryDelay` verifies.

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
timeline, so the served attempt count is never silently truncated. One script has deterministic single-consumer
semantics by default: a second in-flight request fails with `ConcurrentScriptUseException` instead of silently
interleaving outcomes. Use `ScriptConcurrency.AllowConcurrent` when the scenario under test is genuinely concurrent.

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
`-p:ResilienceVersion=`. The core package does not reference `Microsoft.Extensions.Http.Resilience` or Polly: the
terminal handler, the script, the report, and the assertions use only `System.Net.Http`, and the test suite proves the
same contracts with a hand-written delegating handler.

## Configuration Inspection Versus Observable Behaviour

Inspection tools answer "what pipeline did I construct?" by reading options and pipeline descriptors.
KeelMatrix.ResilienceSpec answers "what did this assembled client do?" by counting the attempts that really reached
the network boundary, in order, with their methods and outcomes. Configuration intent and observable behaviour can
differ — for example when a retry predicate, a disabling API, or a timeout interacts with the method of the request —
so this package deliberately asserts the assembled behaviour instead of restating configuration.

## Platforms And Target Frameworks

- Target framework: `net8.0`.
- The package and its tests use portable .NET APIs. Hosted validation runs Full validation on Windows and macOS against
  `10.10.0`; Linux runs the portable core/integration script. Every runner then runs explicit integration checks
  against both `9.8.0` and `10.10.0`.
- Only `net8.0` is exercised. The macOS evidence comes from a hosted, virtualized `macos-latest` runner, not physical
  macOS hardware. Linux package-stage parity is not established by this workflow.

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
| `MissingTimeProviderException` when creating a client | The scenario has a controllable clock but the container has no `TimeProvider`. Register the same instance with `services.AddSingleton<TimeProvider>(clock)`, or set `RequireRegisteredTimeProvider` to `false` when the pipeline time source is configured another way. |
| `MissingTimeProviderException` from a timing assertion | The scenario was created without a clock. Create it with `new ResilienceScenario(script, clock, clock.Advance)`. |
| `ConcurrentScriptUseException` | Two in-flight requests consumed one script. Create one scenario per logical call, or opt in to `ScriptConcurrency.AllowConcurrent`. |
| `ScriptExhaustedException` | The client under test made more attempts than the script describes. Extend the script with `HttpFaultScript.Sequence` or `HttpFaultScript.Always`. |
| `AttemptStateOverflowException` | The client under test served more attempts than `HttpAttemptReport.MaximumRecordedAttempts`, so the recorded timeline is incomplete and cannot judge attempt state. Lower the client's configured maximum attempts; a script cannot describe more attempts than `HttpFaultScript.MaximumSteps` steps. |
| A timing assertion fails by less than one advance step | The observation granularity is `ResilienceScenarioOptions.AdvanceStep`. Lower it for finer observation. |
| A request never settles | The script contains a step that waits for the clock or never answers. Check that the injected clock is registered and that `AdvanceClock` is enabled, or assert `ShouldBePending()`. |

## Documentation

- [docs/DEV.md](docs/DEV.md) documents the local validation path and package gates.
- [CHANGELOG.md](CHANGELOG.md) lists released and unreleased changes.
- [SECURITY.md](SECURITY.md) explains how to report a vulnerability privately.
- [PRIVACY.md](PRIVACY.md) describes telemetry and data handling.
- [CONTRIBUTING.md](CONTRIBUTING.md) documents how to contribute changes.

## License

MIT. See [LICENSE](LICENSE).
