# Development

This document is for maintainers of KeelMatrix.ResilienceSpec. Package consumers should start with the repository
`README.md` or the package README.

## Prerequisites

- The .NET SDK selected by `global.json` (`10.0.401`).
- PowerShell 7 (`pwsh`) for the repository gates.
- The verification path needs no listener, socket, container, or hosted service: scripted scenarios are answered in
  memory. Validation sets `KEELMATRIX_NO_TELEMETRY=1` (see the validation path below), so the optional telemetry
  transport is not exercised either.

## Repository layout

| Path | Purpose |
| --- | --- |
| `src/KeelMatrix.ResilienceSpec` | The shipping library and its public API baseline |
| `tests/KeelMatrix.ResilienceSpec.Tests` | Core behaviour, assertion, privacy, and timing tests |
| `tests/KeelMatrix.ResilienceSpec.IntegrationTests` | Composition with Microsoft's standard resilience handler |
| `tests/PackageSmoke` | Clean consumer that restores the packed package from a local feed |
| `samples/KeelMatrix.ResilienceSpec.Sample` | Runnable walkthrough of the documented quick start |
| `scripts` | Restore, build, test, pack, inspect, smoke, and audit gates |

## Validation path

Run the whole gate:

```powershell
$env:KEELMATRIX_NO_TELEMETRY = '1'
pwsh -NoProfile -File .\scripts\Validate.ps1
```

The gate performs, in order: restore from `NuGet.config`, a formatting/analyzer check, a Release build of
`KeelMatrix.ResilienceSpec.slnx`, the Release test run of both test projects, and the package gate
(`scripts/Invoke-PackageSmoke.ps1`). `-Mode Full` adds `scripts/Invoke-DependencyAudit.ps1 -Mode Required`.

## Hosted CI status

Ordinary push and pull-request CI is intentionally absent while this repository is private and private GitHub Actions
use is not approved. The repository contains only the tag-triggered release workflow at
`.github/workflows/release.yml`; it cannot run from a normal `main` push or pull request. As a result, the validation
evidence below is local-only: hosted runner behaviour, hosted CI checks, and Linux/macOS parity remain unverified.

Useful narrower variants:

```powershell
pwsh -NoProfile -File .\scripts\Validate.ps1 -Mode Focused -SkipPackage
dotnet test .\tests\KeelMatrix.ResilienceSpec.Tests -c Release
dotnet test .\tests\KeelMatrix.ResilienceSpec.IntegrationTests -c Release -p:ResilienceVersion=9.8.0
dotnet run --project .\samples\KeelMatrix.ResilienceSpec.Sample -c Release
```

## Package gate

```powershell
pwsh -NoProfile -File .\scripts\Invoke-PackageSmoke.ps1
```

The script packs the shipping project into `artifacts/packages/feed` with `--include-symbols`, runs
`scripts/Inspect-Package.ps1` against the resulting `.nupkg` and `.snupkg`, then restores `tests/PackageSmoke` with an
isolated package cache and a generated `NuGet.config` whose package source mapping allows the candidate package to
come from the local feed only. It verifies that the restored artifact hash matches the freshly packed file and runs
the consumer, which asserts the documented behaviour and reports runtime transport event counts. The log is written
to `artifacts/packages/package-smoke.log`, which is ignored by Git and never packed.

`scripts/Inspect-Package.ps1` enforces the package contract: the exact archive entry set, package ID, version,
authors, description, tags, license, README, icon, repository and SourceLink commit, the single `net8.0` dependency
group with its exact dependency versions, the portable PDB inside the symbol package, and the absence of any file
that is not on the allowlist.

## Integration range

`Directory.Packages.props` pins `Microsoft.Extensions.Http.Resilience` through the `ResilienceVersion` property. Run
the integration suite once per supported end of the range:

```powershell
dotnet test .\tests\KeelMatrix.ResilienceSpec.IntegrationTests -c Release -p:ResilienceVersion=9.8.0
dotnet test .\tests\KeelMatrix.ResilienceSpec.IntegrationTests -c Release -p:ResilienceVersion=10.10.0
```

## Deterministic timing contract

Timing assertions require a scenario created with a controllable `TimeProvider` and the operation that advances it.
The scenario advances that clock in `ResilienceScenarioOptions.AdvanceStep` increments while a request is pending and
waits one `ObservationWindow` for the pipeline to react, so the wall-clock cost of a run is
`(virtual budget / advance step) * observation window`. Lower `AdvanceStep` for finer observation, and keep
`ObservationWindow` comfortably above thread-pool scheduling latency so a continuation is always observed at the
advance that released it.

`Retry-After` is supported in the delta-seconds form only. The HTTP-date form resolves against the wall clock while
the wait runs on the injected clock, so it is deliberately not exposed.

## Dependency audit evidence

`scripts/Invoke-DependencyAudit.ps1 -Mode Required` fails closed unless direct and transitive advisory data is
available. On 2026-09-16, a bounded required-audit attempt and a bounded no-restore advisory query could not reach
NuGet.org from this environment: the advisory/flat-container socket was denied before authoritative results were
returned. The repository therefore makes no claim that the dependency graph is clean.

This is an accepted preparation-only environment risk, scoped to the missing advisory response. The mitigation is to
rerun the required audit from a network-enabled isolated environment before any release tag or publication, keep the
required mode fail-closed, and treat a non-zero result as a release blocker. No release action is authorized by this
note.

## Release preparation

Before a release: promote accepted `PublicAPI.Unshipped.txt` entries into `PublicAPI.Shipped.txt`, finalize the
`CHANGELOG.md` entry for the target version, and re-run the full gate on the resulting commit. Tagging, publishing,
and repository visibility changes are separate, explicitly approved steps.
