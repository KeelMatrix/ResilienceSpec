# Repository guide

## Navigation

- `src/ResilienceSpec.Probe.Core` contains the non-shipping probe primitives: the scripted in-memory terminal
  handler, the observation timeline, the recording delegating handler, and the runtime network-event observer.
  It references no package and uses only `System.Net.Http`.
- `tests/ResilienceSpec.Probe.Runner` assembles the probe chains, references the resilience packages as
  probe-only development dependencies, and prints the evidence.
- `tests/ResilienceSpec.Probe.Runner/Probes` contains one file per measured item.

## Commands

```text
dotnet restore ResilienceSpec.Probe.sln --configfile NuGet.config
dotnet build ResilienceSpec.Probe.sln -c Release --no-restore
dotnet run --project tests/ResilienceSpec.Probe.Runner -c Release --no-build
dotnet run --project tests/ResilienceSpec.Probe.Runner -c Release -- chain truth
```

## Invariants

- Every project stays non-packable. No package, product public API, workflow, analyzer, or CLI belongs here.
- The assembled client under test keeps its real handler chain; only the innermost network boundary is
  replaced by the in-memory terminal handler. Never bypass or duplicate the resilience layer in a probe.
- Only public APIs may be used. Never use reflection over Polly or Microsoft types, including for
  diagnostics.
- No probe may use a fixed sleep or an elapsed-time tolerance to make a timing claim true. Bounded observation
  windows may only distinguish "completed" from "still pending"; wall-clock values are reference data.
- Scripted requests target the reserved `.invalid` domain and are answered in memory. The probe must not
  resolve names, open sockets, or bind listeners.
- Attempt records stay minimal: ordinal, method, scripted outcome, and relative timing only. Never record
  request or response payloads, headers, or query strings.
- The runner must stay the single full-probe command and must fail when a hard expectation does not hold.

## Validation

Run the focused probe first while developing, then a Release build of the solution and the complete runner
before handing evidence on. Probe output is the evidence; do not paste raw research notes into the repository.

Repository validation is local by design; no automated workflow is configured here.
