# Development

This document is for maintainers of KeelMatrix.ResilienceSpec. Package consumers should start with the repository
`README.md` or the package README.

## Prerequisites

- The exact .NET SDK pinned by `global.json` (`10.0.401`); SDK roll-forward is disabled for package identity evidence.
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

The script restores `KeelMatrix.ResilienceSpec.slnx` with `NuGet.config`, runs the core and integration Release tests,
then runs `scripts/Invoke-PackageSmoke.ps1` and `scripts/Run-Sample.ps1`. The package smoke includes pack, package
inspection, and an isolated clean-consumer restore. Linux does not run the formatting gate or the dependency audit.
Windows and macOS use `scripts/Validate.ps1 -Mode Full -ResilienceVersion 10.10.0`, which includes formatting, the same
package smoke and sample stages, and the required dependency audit.

## Hosted CI status

The repository contains `.github/workflows/validate.yml`, which runs on pushes to `main` and on manual dispatch with
Windows, Ubuntu, and macOS hosted runners. Windows and macOS invoke Full validation against `10.10.0`; Linux runs its
repository-controlled test, package-smoke, and sample path without the format or audit stages. Every runner then runs
explicit integration jobs for both `9.8.0` and `10.10.0`. Only `net8.0` is exercised; a hosted result is evidence from
the specific runner, not physical hardware.

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

The script requires the exact .NET SDK pinned by `global.json`, then packs the shipping project twice with
`--include-symbols`, normalizes both archives to the fixed ZIP-local timestamp `1980-01-01 00:00:00`, stored
(uncompressed) entries, and LF-only UTF-8 text payloads. It fails if either the `.nupkg` or `.snupkg` SHA256 changes
between packs, and prints a sorted entry-level manifest plus its canonical identity SHA256, including both generated
nuspec entries. It then
copies the first normalized pair into `artifacts/packages/feed`, runs `scripts/Inspect-Package.ps1` against those exact
artifacts, and restores `tests/PackageSmoke` with an isolated package cache and a generated `NuGet.config` whose
package source mapping allows the candidate package to come from the local feed only. The consumer restore hash must
match the inspected package, and the consumer asserts the documented behaviour and reports runtime transport event
counts. The reproducible artifact hashes and smoke output are written to `artifacts/packages/package-smoke.log`, which
is ignored by Git and never packed.

`Directory.Build.props` maps the repository source root to `/_/` for deterministic compiler and portable-PDB paths;
`Directory.Build.targets` runs `scripts/Normalize-GeneratedSources.ps1` before compilation so SDK-generated source
line endings are also stable. Together these keep the compiled package payload independent of the checkout directory
and operating system.

`scripts/Inspect-Package.ps1` enforces the package contract: normalized archive timestamps and stored entries, LF-only
UTF-8 XML/package metadata, documentation, and license entries, the exact archive entry set, package ID, version, authors,
description, tags, license, README, icon, repository and SourceLink
commit, the single `net8.0` dependency group with its exact dependency versions, the portable PDB inside the symbol
package, and the absence of any file that is not on the allowlist.

## Sample package flow

```powershell
pwsh -NoProfile -File .\scripts\Run-Sample.ps1
```

The sample gate packs `KeelMatrix.ResilienceSpec` to a temporary local feed, normalizes the package and symbol
archives with the same fixed ZIP-local timestamp, stored-entry, and LF-only UTF-8 text format used by the package gate, maps only that package ID to the feed, restores all
other dependencies from NuGet.org into an isolated cache, verifies the restored package hash, and runs the sample with
`--no-restore`. The temporary feed and cache are removed when the command finishes.

## Integration range

`Directory.Packages.props` pins `Microsoft.Extensions.Http.Resilience` through the `ResilienceVersion` property. Run
the integration suite once per supported end of the range; hosted validation runs both endpoints explicitly:

```powershell
dotnet test .\tests\KeelMatrix.ResilienceSpec.IntegrationTests -c Release -p:ResilienceVersion=9.8.0
dotnet test .\tests\KeelMatrix.ResilienceSpec.IntegrationTests -c Release -p:ResilienceVersion=10.10.0
```

## Deterministic timing contract

Timing assertions require a `ResilienceScenarioClock` around an exact
`Microsoft.Extensions.Time.Testing.FakeTimeProvider` from `Microsoft.Extensions.TimeProvider.Testing` `10.10.0` used
by the client pipeline. Other testing-package versions are unverified and rejected by admission. Keep
`AutoAdvanceAmount` at zero, register `clock.TimeProvider`, and pass the clock object to the scenario. The scenario
invokes the wrapper's verified advance operation itself. The wrapper verifies that its delegate moves the admitted provider once by exactly the requested duration and detects
direct or other unexpected provider movement. Admission explicitly loads
`Microsoft.Extensions.TimeProvider.Testing.dll` version `10.10.0.0` from the dependency path beside the package
assembly, checks the expected Microsoft strong-name public-key token, and compares the provider type with the type from
that assembly; `TimeProvider.System`, consumer-authored derived or delegating providers, same-name assemblies from
another path, and resolver-hook substitutions are rejected. The check does not attest a consumer-replaced file at that
exact path. The scenario advances directly to the next tracked provider timer when available and uses `AdvanceStep`
only when no timer deadline is available. Exact assertions compare injected-clock values for equality and reject the
specific observation if fallback sampling affected it; `AdvanceStep` is not a tolerance. It waits for
scripted-downstream progress only after a timer fires; if a fired timer's
continuation does not reach the scripted downstream within `ObservationWindow`, the scenario returns `Pending` without
another virtual advance. The observation window is a watchdog, not a timing measurement. A virtual-budget or no-advance
cutoff returns `Pending` and is not reported as request settlement. Cancellation cleanup is separately bounded by
`ResilienceScenarioOptions.CleanupTimeout`; late cleanup releases retained resources, but a scenario remains consumed
and cannot be reused after cleanup.

`Retry-After` is supported in the delta-seconds form only. The HTTP-date form resolves against the wall clock while
the wait runs on the injected clock, so it is deliberately not exposed.

## Dependency audit evidence

`scripts/Invoke-DependencyAudit.ps1 -Mode Required` fails closed unless direct and transitive advisory data is
available and every project reports no vulnerable packages from `https://api.nuget.org/v3/index.json`. Windows and
macOS Full validation run this audit; Linux does not. Audit evidence is valid only for the exact candidate and hosted
run that produced it, so use the current candidate's CI conclusion rather than a dated statement in this document.
Treat a non-zero result as a release blocker. No release action is authorized by a passing audit.

## Release preparation

Before a release: promote accepted `PublicAPI.Unshipped.txt` entries into `PublicAPI.Shipped.txt`, finalize the
`CHANGELOG.md` entry for the target version, and re-run the full gate on the resulting commit. Tagging, publishing,
and repository visibility changes are separate, explicitly approved steps.
