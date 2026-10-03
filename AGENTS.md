# Repository guide

## Navigation

- `src/KeelMatrix.ResilienceSpec` is the only packable project. It contains the script, the scripted terminal
  handler, the scenario, the attempt report, the assertions, the HttpClientFactory adapter, and the telemetry entry
  point.
- `tests/KeelMatrix.ResilienceSpec.Tests` proves the core contracts with hand-written delegating handlers, so the
  core package is exercised without any resilience library.
- `tests/KeelMatrix.ResilienceSpec.IntegrationTests` wires the real `Microsoft.Extensions.Http.Resilience` standard
  handler to the scripted downstream.
- `tests/PackageSmoke` is a clean consumer that restores the packed package from an isolated local feed. It is
  deliberately outside the solution.
- `samples/KeelMatrix.ResilienceSpec.Sample` is a runnable walkthrough of the documented quick start.
- `scripts` contains the repository gates: `Validate.ps1`, `Invoke-ReleaseWorkflow.ps1`, `Invoke-PackageSmoke.ps1`, `Inspect-Package.ps1`,
  `Invoke-IntegrationTests.ps1`, `Invoke-DependencyAudit.ps1`, `Validate-History.ps1`, `Validate-DocumentationHygiene.ps1`,
  `Validate-ReleaseProvenance.ps1`, and `Validate-ReleaseContract.ps1`.
- `docs/DEV.md` explains the local validation path; `README.md` and `src/KeelMatrix.ResilienceSpec/README.md` are the
  user-facing documentation.

## Commands

```text
pwsh -NoProfile -File scripts/Validate.ps1
pwsh -NoProfile -File build/Test-ExternalCommandRouting.ps1
pwsh -NoProfile -File scripts/Validate.ps1 -Mode Focused -SkipPackage
dotnet test tests/KeelMatrix.ResilienceSpec.Tests -c Release
dotnet test tests/KeelMatrix.ResilienceSpec.IntegrationTests -c Release -p:ResilienceVersion=9.8.0
pwsh -NoProfile -File scripts/Invoke-PackageSmoke.ps1
pwsh -NoProfile -File scripts/Validate-ReleaseContract.ps1 -Tag v0.1.0
```

## Invariants

- The terminal handler replaces only the network boundary. Never bypass, replace, or duplicate the resilience layer
  of the client under test, and never depend on reflection over Polly or Microsoft internals for correctness.
- The implementation-neutral script, report, and assertion core does not depend on Polly or
  `Microsoft.Extensions.Http.Resilience`. The shipping package intentionally references `Microsoft.Extensions.Http`
  for its `IHttpClientFactory` adapter and `KeelMatrix.Telemetry`; Polly and
  `Microsoft.Extensions.Http.Resilience` remain outside the runtime package dependency graph.
- Scripts, attempt records, and diagnostics stay privacy-safe: ordinal, method, broad outcome, scripted status or
  `Retry-After` value, and injected-clock timing only. Never record or echo URIs, query strings, headers, cookies,
  authorization values, bodies, or exception messages.
- Timing assertions run on an injected clock only. Never introduce a wall-clock sleep, an elapsed-time tolerance, or a
  silent fallback that makes a timing claim true. Public timestamp evidence rejects non-exact movement and reentrant
  timer-callback mutation. The public `TimeProvider` contract cannot identify an external exact-sum mutation during an
  advance, so callers must keep exclusive ownership of the underlying provider and that sharing pattern is not a
  proven timing result. A known timer deadline at or below the remaining virtual budget is targeted directly; a
  deadline above it remains an honest observation cutoff.
- Scripted delays use whole-millisecond precision and reject values above the controlled timer's `int.MaxValue`
  millisecond limit. `Retry-After` uses integer whole-second HTTP wire values, rejects fractional or sub-second
  values, and keeps its HTTP delta-seconds limit separate from the timer limit; observation, pending, and cleanup
  durations are separate watchdog inputs with the same Task.Delay range contract.
- Parameterless scripts, attempt state, and timelines stay bounded, and each scenario keeps permanent single-logical-call
  semantics. Only `ResilienceScenario.SendAsync` starts the call. Direct handler, manual-client, factory-client, and
  invoker sends fail with `ScenarioConsumedException` before consuming a step or mutating the report; genuine retries
  inherit the active call lease, and deliberate request clones must preserve the source request options containing its
  opaque lease marker. The marker remains sufficient when execution-context flow is deliberately suppressed. Fresh
  unmarked requests are rejected even when an ambient execution context flows. Create one scenario per logical call.
- Timing origin and exact-evidence state begin when the logical call is admitted, not when the scenario is constructed;
  prepared scenarios sharing a clock therefore keep independent elapsed-time reports. Initial sends and provider clock
  advances are observed behind bounded watchdogs, with late cleanup retaining the logical-call lease.
- Telemetry policy: An activation is requested only after a settled scenario has either observed an injected failure
  published by an executed attempt, an executed injected timeout classified as a timeout by the settled client or
  strategy, or a positively recognized native HttpClient.Timeout outcome with at least one executed attempt, and an
  assertion is evaluated at or after settlement. ResponseFault is true only for an HTTP response with status 400 or
  higher published by an executed attempt. ExceptionFault is true only for a network failure published by an
  executed attempt, an executed injected timeout classified as a timeout by the settled client or strategy, or a
  positively recognized native HttpClient.Timeout outcome whose cancellation token is the same token passed to an
  executed scripted attempt and is canceled. Plain caller cancellation, arbitrary upstream timeout exceptions
  (including native-shaped exceptions without that token evidence), unexecuted script steps, and observation
  cleanup do not set ExceptionFault. Failure categories accumulate across executed attempts: any case with a
  published response or network category produces one signal containing the accumulated categories, while a case
  with no category produces zero sink signals.
- Public API changes are recorded in the analyzer baseline next to the package project; new API goes to
  `PublicAPI.Unshipped.txt` and is promoted to `PublicAPI.Shipped.txt` during release preparation.
- The package must keep building and testing offline: no listener, socket, DNS lookup, container, or hosted service is
  allowed on the validation path.
- Every external validation gate runs through `build/Invoke-ExternalCommand.ps1` and its verified runtime shims in
  `build/command-shims`. Windows Job Objects and Linux cgroup
  v2/child-subreaper process-tree inspection claim provable whole-tree containment. macOS uses a session/process-group
  boundary plus descendant inspection and reports that containment kind and limitation explicitly: a process that
  deliberately creates a new session can escape that boundary. It captures both streams completely, reports
  exit/timeout/termination/descendant state explicitly, and fails closed for every detectable containment, inspection,
  probe, termination, or cleanup error. Any such error forces failure and `DescendantsContained=false`. It prints
  stdout before stderr; cross-stream chronology is not part of the contract. The shared nested-launch guard also scans
  C# `ProcessStartInfo`/`Process.Start` sites and requires inspectable, no-shell, redirected, no-window launch settings;
  the scan fails closed when no C# launch sites are found.
- `.github/workflows/validate.yml` and `.github/workflows/release.yml` may invoke repository scripts, but may not
  contain direct Git, .NET, or shell validation gates. `build/Test-ExternalCommandWorkflow.ps1` is the source of
  truth for this workflow routing guard; the runtime shim plus bounded runner is the source of truth for process-level
  routing. Release verification and publication use `scripts/Invoke-ReleaseWorkflow.ps1`. The Linux shell entrypoint
  has only an outer PowerShell bootstrap using the executable resolved before the shim is installed; subsequent
  integration scripts use `build/Invoke-ExternalScript.ps1`. Runner-authorized descendants inherit the verified real
  target paths only inside the already-contained process tree. Absolute paths, forged authorization, and separate environments remain
  residual forms covered by the static guards rather than the PATH shim.
- Set `KEELMATRIX_NO_TELEMETRY=1` for local validation. Repository validation must never emit production telemetry.
- Release-tag validation requires `[Unreleased]` to contain only its heading and blank lines. Reachable commit history
  is also checked for non-product provenance or authorship metadata, prohibited trailers, and non-conforming
  authorship; the GitHub web-flow `KeelMatrix` author / `GitHub` committer exception remains valid.
- The verified Microsoft.Extensions.Http.Resilience versions are **9.8.0** and **10.10.0**. Other versions are unverified.
  `build/ResilienceCompatibility.props` is the source of truth and its drift validator covers docs, workflows, the DI
  dependency/API, the delta-only timing scope, and the weekly telemetry heartbeat.

## Validation

Run the focused test project while developing, then `scripts/Validate.ps1` before handing work on. The script checks
reachable history, restores,
verifies formatting, builds Release, runs both test projects, packs and inspects the package, and runs the clean
consumer smoke from an isolated local feed. `-Mode Full` adds the dependency vulnerability audit. Package and
validation output goes to the ignored `artifacts/` directory.
