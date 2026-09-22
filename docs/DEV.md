# Development

This document is for maintainers of KeelMatrix.ResilienceSpec. Package consumers should start with the repository
`README.md` or the package README.

## Prerequisites

- The .NET SDK selected by `global.json` (`10.0.401`).
- PowerShell 7 (`pwsh`) for the repository gates.
- Bash for the Linux validation script.
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
`KeelMatrix.ResilienceSpec.slnx`, sequential Release test runs of the core and integration projects, and the package gate
(`scripts/Invoke-PackageSmoke.ps1` plus `scripts/Run-Sample.ps1`). `-Mode Full` adds
`scripts/Invoke-DependencyAudit.ps1 -Mode Required`. The sample is intentionally outside the solution because it
restores the shipping package from its own temporary local feed.

## Linux validation

The repository-controlled Linux check requires the .NET SDK selected by `global.json`, PowerShell 7 (`pwsh`) for the
release-contract tests, and Bash. It does not require Docker, a listener, or any external service. From a Linux shell,
run:

```bash
bash ./scripts/validate-linux.sh
```

The script restores `KeelMatrix.ResilienceSpec.slnx` with `NuGet.config`, runs the core Release tests, and runs the
integration Release tests when that project is present. The hosted Linux job uses this lightweight path; it does not run
package inspection, the clean consumer smoke test, or the sample. Windows and macOS use
`scripts/Validate.ps1 -Mode Full -ResilienceVersion 10.10.0`, including those package stages and the required audit.

## Hosted CI status

The repository contains `.github/workflows/validate.yml`, which runs on pushes to `main` and on manual dispatch with
Windows, Ubuntu, and macOS hosted runners. Windows and macOS invoke Full validation against `10.10.0`; Linux runs its
portable core/integration script. Every runner then runs explicit integration jobs for both `9.8.0` and `10.10.0`. Only
`net8.0` is exercised; a hosted result is evidence from the specific runner, not physical hardware.

Useful narrower variants:

```powershell
pwsh -NoProfile -File .\scripts\Validate.ps1 -Mode Focused -SkipPackage
dotnet test .\tests\KeelMatrix.ResilienceSpec.Tests -c Release
dotnet test .\tests\KeelMatrix.ResilienceSpec.IntegrationTests -c Release -p:ResilienceVersion=9.8.0
pwsh -NoProfile -File .\scripts\Run-Sample.ps1
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

## Sample package flow

```powershell
pwsh -NoProfile -File .\scripts\Run-Sample.ps1
```

The sample gate packs `KeelMatrix.ResilienceSpec` to a temporary local feed, maps only that package ID to the feed,
restores all other dependencies from NuGet.org into an isolated cache, verifies the restored package hash, and runs
the sample with `--no-restore`. The temporary feed and cache are removed when the command finishes.

## Integration range

`Directory.Packages.props` pins `Microsoft.Extensions.Http.Resilience` through the `ResilienceVersion` property. Run
the integration suite once per supported end of the range; hosted validation runs both endpoints explicitly:

```powershell
dotnet test .\tests\KeelMatrix.ResilienceSpec.IntegrationTests -c Release -p:ResilienceVersion=9.8.0
dotnet test .\tests\KeelMatrix.ResilienceSpec.IntegrationTests -c Release -p:ResilienceVersion=10.10.0
```

## Deterministic timing contract

Timing assertions require a `ResilienceScenarioClock` around the controllable `TimeProvider` used by the client
pipeline. Register `clock.TimeProvider` and pass that provider plus `clock.Advance` to the scenario. The scenario
advances directly to the next tracked provider timer when available and uses `AdvanceStep` only when no timer deadline
is available. It waits for scripted-downstream progress only after a timer fires; if a fired timer's continuation does
not reach the scripted downstream within `ObservationWindow`, the scenario returns `Pending` without another virtual
advance. The observation window is a watchdog, not a timing measurement. A virtual-budget or no-advance cutoff returns `Pending` and is not reported as
request settlement. Cancellation cleanup is separately bounded by `ResilienceScenarioOptions.CleanupTimeout`; the
single-consumer lease remains held until late cleanup finishes.

`Retry-After` is supported in the delta-seconds form only. The HTTP-date form resolves against the wall clock while
the wait runs on the injected clock, so it is deliberately not exposed.

## Dependency audit evidence

`scripts/Invoke-DependencyAudit.ps1 -Mode Required` fails closed unless direct and transitive advisory data is
available. The audit previously identified `Microsoft.Build.Tasks.Git 8.0.0`, brought in by the private
`Microsoft.SourceLink.GitHub 8.0.0` build dependency, through advisory `GHSA-23fw-v26w-5fgq`. The repository now
uses `Microsoft.SourceLink.GitHub 10.0.401`, aligned with the selected .NET SDK. The final required audit passed on
2026-09-16: all three projects reported no vulnerable packages from `https://api.nuget.org/v3/index.json`.

The earlier advisory finding is resolved by the SourceLink update and the passing audit. Keep the required mode
fail-closed and rerun it before any release tag or publication; treat a non-zero result as a release blocker. No
release action is authorized by this note.

## Release preparation

Before a release: promote accepted `PublicAPI.Unshipped.txt` entries into `PublicAPI.Shipped.txt`, finalize the
`CHANGELOG.md` entry for the target version, and re-run the full gate on the resulting commit. Tagging, publishing,
and repository visibility changes are separate, explicitly approved steps.
