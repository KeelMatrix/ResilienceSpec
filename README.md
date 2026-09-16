# KeelMatrix.ResilienceSpec

This repository contains the feasibility probe for a proposed HttpClient resilience verifier. It is **not a
shipping package**, it defines no public product API, and it produces no NuGet artifact. Every project in the
solution is explicitly non-packable.

The probe answers one question with measurements instead of assumptions:

> Can a deterministic in-memory terminal handler sit behind a real `IHttpClientFactory` handler chain that
> contains `Microsoft.Extensions.Http.Resilience`, and can retry, `Retry-After`, per-attempt timeout, and
> total-request timeout behaviour be made deterministic through supported public APIs - with no reflection
> into resilience internals and no wall-clock timing tolerances?

## What is measured

| Probe | Measurement |
| --- | --- |
| `chain` | Handler order and attempt counts for a scripted `503` then `200`, plus a negative control with the resilience handler removed. |
| `truth` | Attempt count, final status, and reference wall-clock delay for `503` then `200`, and the retry ceiling for an always-failing downstream. |
| `unsafe` | Default POST retry behaviour, the public API that disables unsafe-method retries, a safe-method control, and a POST that fails through the per-attempt timeout. |
| `virtualtime` | Whether a registered `TimeProvider` reaches the standard resilience handler, and whether the public pipeline builder's `TimeProvider` drives the retry delay. |
| `retryafter` | `Retry-After` as delta seconds and as an HTTP-date, including which clock resolves each form. |
| `timeouts` | Whether per-attempt and total-request timeouts fire on the controlled clock, and that they never hang. |
| `network` | Runtime network event counters observed during the run, plus the in-memory attempt total. |
| `versions` | Runtime, host, and the assembly versions actually loaded by the run. |

## Running the probe

```text
dotnet restore ResilienceSpec.Probe.sln --configfile NuGet.config
dotnet build ResilienceSpec.Probe.sln -c Release --no-restore
dotnet run --project tests/ResilienceSpec.Probe.Runner -c Release --no-build
```

Running every project of the solution and then the runner is the complete probe. Individual probes can be run
for a shorter loop by naming them, for example:

```text
dotnet run --project tests/ResilienceSpec.Probe.Runner -c Release -- chain
```

The probe-only dependency versions are centralized in `Directory.Packages.props` and can be overridden to
measure another published version:

```text
dotnet run --project tests/ResilienceSpec.Probe.Runner -c Release -p:ProbeResilienceVersion=9.10.0
```

## Interpreting the output

Each probe prints facts, then hard expectations, then a verdict per gate item. `PASS` means the measured
behaviour matches the claim, `NARROW` means only a named subset is feasible, and `FAIL` means the claim did not
hold. Hard expectations are the facts the harness itself must satisfy; a failed expectation fails the run.

Bounded observation windows (at most a few seconds) are used only to distinguish "completed" from "still
pending" while a controlled clock is advanced. No assertion in this probe depends on an elapsed-time
tolerance, and no probe sleeps to make a timing claim true. Wall-clock numbers are printed as reference values
only.

## Network behaviour

Requests use the reserved `.invalid` top-level domain and are answered by an in-memory `HttpMessageHandler`, so
a request that left the process would fail name resolution instead of reaching the terminal handler. The
`network` probe reports the runtime network events observed during the run and the number of attempts answered
in memory. The probe never binds a listener.

## Scope

This repository is a bounded feasibility probe. Do not add a shipping package, product public API, analyzer,
CLI, workflow, or telemetry wiring here before the product scope is decided and implemented separately.

The resilience packages referenced here are probe-only development dependencies. The in-memory terminal
handler and its records use only `System.Net.Http`.
