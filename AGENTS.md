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
- `scripts` contains the repository gates: `Validate.ps1`, `Invoke-PackageSmoke.ps1`, `Inspect-Package.ps1`,
  `Invoke-DependencyAudit.ps1`, and `Validate-ReleaseContract.ps1`.
- `docs/DEV.md` explains the local validation path; `README.md` and `src/KeelMatrix.ResilienceSpec/README.md` are the
  user-facing documentation.

## Commands

```text
pwsh -NoProfile -File scripts/Validate.ps1
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
  silent fallback that makes a timing claim true.
- Parameterless scripts, attempt state, and timelines stay bounded, and one script keeps single-consumer semantics by
  default.
- Public API changes are recorded in the analyzer baseline next to the package project; new API goes to
  `PublicAPI.Unshipped.txt` and is promoted to `PublicAPI.Shipped.txt` during release preparation.
- The package must keep building and testing offline: no listener, socket, DNS lookup, container, or hosted service is
  allowed on the validation path.
- Set `KEELMATRIX_NO_TELEMETRY=1` for local validation. Repository validation must never emit production telemetry.

## Validation

Run the focused test project while developing, then `scripts/Validate.ps1` before handing work on. The script restores,
verifies formatting, builds Release, runs both test projects, packs and inspects the package, and runs the clean
consumer smoke from an isolated local feed. `-Mode Full` adds the dependency vulnerability audit. Package and
validation output goes to the ignored `artifacts/` directory.
