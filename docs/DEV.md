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

## Logical-call contract

One `ResilienceScenario` owns exactly one logical operation, and `ResilienceScenario.SendAsync` is the only supported
root entry point. `scenario.Handler` can be installed in a manual `HttpClient` or by
`UseResilienceSpecDownstream`, but every operation must still be started through `scenario.SendAsync`. Direct
`HttpClient`/factory-client/`HttpMessageInvoker` sends fail with `ScenarioConsumedException` before consuming a script
step or mutating the report. Retries and timeouts generated inside the configured handler chain inherit the active
lease. Deliberate request clones must preserve the source request options, including the opaque logical-call marker;
that marker remains sufficient even when execution-context flow is deliberately suppressed. Fresh unmarked requests
created inside flowed handler context are rejected before script/report mutation. Settlement,
cancellation, timeout, and observation cutoff all leave the scenario permanently consumed.

## Validation path

Run the whole gate:

```powershell
$env:KEELMATRIX_NO_TELEMETRY = '1'
pwsh -NoProfile -File .\scripts\Validate.ps1
```

The gate performs, in order: reachable-history hygiene, restore from `NuGet.config`, a formatting/analyzer check, a Release build of
`KeelMatrix.ResilienceSpec.slnx`, sequential Release test runs of the core and integration projects, and the package gate
(`scripts/Invoke-PackageSmoke.ps1` plus `scripts/Run-Sample.ps1`). `-Mode Full` adds
`scripts/Invoke-DependencyAudit.ps1 -Mode Required`. The sample is intentionally outside the solution because it
restores the shipping package from its own temporary local feed.

### Runtime external-command routing

The routing contract has one source of truth: `build/Invoke-ExternalCommand.ps1` together with the regular files in
`build/command-shims`. When the runner is loaded, it verifies that `dotnet`, `git`, and `pwsh` resolve to real
executables after the shim directory is removed from `PATH`, verifies every required shim (including Unix execute
permission), and then prepends the repository shim directory to the process `PATH`. Missing, malformed,
non-executable, or unresolvable shim state fails closed.

The shims reject calls that do not carry the runner's per-child authorization marker, using exit code 86 and an
actionable routing diagnostic. The runner verifies the real target paths, launches the requested executable directly,
and passes that authorization only into the already-contained process tree; descendants therefore remain under the
same Windows Job Object or Unix containment boundary. The `pwsh` shim additionally requires `-NoProfile -File` for
an existing script. The containment, timeout, capture, termination, and cleanup proof is unchanged.
`build/Test-ExternalCommandRouting.ps1` clears authorization for its direct, alias, and wrapper negative controls and
exercises the positive bounded-runner path.

The Linux shell entrypoint establishes the repository shim directory after resolving the real PowerShell executable,
then uses that already-resolved executable only for the outer PowerShell bootstrap. Its integration scripts are
started by `build/Invoke-ExternalScript.ps1`, which routes them through the bounded runner. Once the repository
scripts are running, the `pwsh` PATH shim rejects unauthorised calls just like the `dotnet` and `git` shims. Absolute
executable paths, forged authorization variables, or commands launched in a separate shell/environment that does not
inherit the repository `PATH` remain outside the shim's guarantee; the static workflow and nested-launch guards remain
defence-in-depth for those residual forms.

The documentation hygiene contract is defined here. The step scans every tracked relative path and file name, then applies
strict decoding and content validation rather than guessing from an extension, a magic prefix, a NUL, or a byte count. It
attempts UTF-8 (including a UTF-8 BOM), ASCII, BOM-aware UTF 16 LE/BE and UTF 32 LE/BE, and bounded BOM-less UTF 16/UTF 32
candidates. Every successful text decode is scanned, including NUL-rich text and every supported encoding that succeeds.

The bounded guarantee is: all text-decodable tracked files are scanned, and every declared PNG is structurally validated and
has all supported PNG textual metadata scanned for prohibited patterns. This metadata includes `tEXt`, `zTXt`, and `iTXt`
keywords and text, including iTXt language tags, translated keywords, and compressed or uncompressed text. A manifest
declaration is authoritative: a declared PNG is validated as PNG before any text decoder can classify it, so clean text under
a PNG declaration is rejected. PNG ancillary chunks whose text semantics are not supported are rejected as undecidable.

The residual boundary is explicit rather than absolute: content encoded inside opaque or compressed binary payloads that is
not text and is not PNG textual metadata is outside this scan surface. Undecidable content is never silently excluded. The
only declared binary format is `png`, and the current manifest declares only the repository-root `icon.png`. Adding another
binary format requires extending the guard and its positive, negative, malformed, and fail-closed controls before it may be
declared.

| Content state | Guard decision |
| --- | --- |
| One or more supported strict decoders succeed and the path is not declared | Scan every successful decoded text; any prohibited term fails the guard. |
| The path is declared | Validate the declared format first; for PNG, scan supported textual metadata and skip only after complete validation succeeds. |
| No decoder succeeds and the tracked path is not in the manifest | Fail closed; the file is not skipped. |
| A declared asset is malformed, truncated, has invalid structure, or has invalid content checks | Fail closed. |

The manifest is the only binary allowlist, and `png` is the only permitted format. Adding another binary format requires a
repository-owned guard extension and complete positive, negative, malformed, and fail-closed controls before the manifest may
declare it.

## Linux validation

The repository-controlled Linux check requires the .NET SDK selected by `global.json`, PowerShell 7 (`pwsh`) for the
release-contract tests, and Bash. It does not require Docker, a listener, or any external service. From a Linux shell,
run:

```bash
bash ./scripts/validate-linux.sh
```

The script delegates to `scripts/Validate.ps1 -Mode Full -ResilienceVersion 10.10.0`, so Linux executes the same
reachable-history, compatibility, restore, formatting, Release build and test, package smoke, sample, and required
dependency-audit gates as Windows. The package smoke includes pack, package inspection, and an isolated
clean-consumer restore.

## Hosted CI status

The repository contains `.github/workflows/validate.yml`, which runs on pushes to `main` and on manual dispatch with
Windows, Ubuntu, and macOS hosted runners. Every runner invokes Full validation against `10.10.0` and then runs explicit
integration jobs for both `9.8.0` and `10.10.0`. Only `net8.0` is exercised; a hosted result is evidence from the
specific runner, not physical hardware. The Windows Full-validation step is bounded to 15 minutes, and each direct
integration step is bounded to 5 minutes; a timeout fails the named step rather than waiting for the matrix job's
outer limit. macOS validation uses a session/process-group boundary and a bounded descendant walk. The runner reports
that containment kind and limitation explicitly: a process that deliberately creates a new session can escape the
macOS boundary. Detectable containment, inspection, termination, cleanup, and exit-status errors fail closed.

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

`build/ResilienceCompatibility.props` is the canonical source for the first-party integration versions. The verified Microsoft.Extensions.Http.Resilience versions are **9.8.0** and **10.10.0**. Other versions are unverified. `Directory.Packages.props` imports that source through the `ResilienceVersion` property. Run the integration suite once per verified endpoint; hosted validation runs both explicitly:

```powershell
pwsh -NoProfile -File .\scripts\Invoke-IntegrationTests.ps1 -ResilienceVersion 9.8.0
pwsh -NoProfile -File .\scripts\Invoke-IntegrationTests.ps1 -ResilienceVersion 10.10.0
```

## Deterministic timing contract

Timing assertions require a `ResilienceScenarioClock` around a controllable `TimeProvider`, such as the public
`Microsoft.Extensions.Time.Testing.FakeTimeProvider` API used by the integration fixtures. Keep automatic advancement
disabled, register `clock.TimeProvider`, and pass the clock object to the scenario. The scenario invokes the wrapper's
verified advance operation itself. The wrapper compares public `GetTimestamp`/`GetElapsedTime` values and rejects
non-exact net movement; movement from a tracked timer callback is also rejected. The public `TimeProvider` contract
cannot identify an external exact-sum mutation during the same advance, so callers must keep exclusive ownership of
the underlying provider and must not treat that sharing pattern as proven timing evidence. `TimeProvider.System`
remains unsupported. The scenario advances directly to the next tracked provider timer when available, including when
the deadline exactly equals the remaining `VirtualBudget`, and uses `AdvanceStep` only when no timer deadline is
available. A deadline beyond the remaining budget remains an honest cutoff. Exact assertions compare injected-clock
values for equality and reject the specific observation if fallback sampling affected it; `AdvanceStep` is not a
tolerance. It waits for
scripted-downstream progress only after a timer fires. A continuation that schedules another legitimate timer may reach
that next deadline; a completed or disabled one-shot timer does not leave a phantom deadline. If no progress and no real
tracked deadline remain within `ObservationWindow`, the scenario returns `Pending` without another virtual advance. The
observation window is a watchdog, not a timing measurement. A virtual-budget or no-advance cutoff returns `Pending` and
is not reported as request settlement. Cancellation cleanup is separately bounded by
`ResilienceScenarioOptions.CleanupTimeout`; late cleanup releases retained resources, but a scenario remains consumed
and cannot be reused after cleanup. The initial client invocation and each injected-clock advance execute behind the
bounded observation watchdog, so a synchronous timer callback cannot block the observer indefinitely; late work remains
observed until the logical-call lease can be released. Optional activation telemetry uses the same lifecycle boundary.
Telemetry policy: An activation is requested only after a settled scenario has either observed an injected failure
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

Scripted `HttpFault.Delay` durations accept whole milliseconds only and are capped at
`TimeSpan.FromMilliseconds(int.MaxValue)`. `Retry-After` deltas use integer whole-second wire values and are bounded
by the HTTP delta-seconds limit; this header limit is separate from the controlled timer limit. Construction rejects
fractional or otherwise unrepresentable header values instead of rounding them. `AdvanceStep` and `VirtualBudget` describe injected virtual time; `ObservationWindow`,
`PendingObservation`, and `CleanupTimeout` are millisecond-precision wall-clock watchdogs.

`Retry-After` is supported in the delta-seconds form only. The HTTP-date form resolves against the wall clock while
the wait runs on the injected clock, so it is deliberately not exposed.

## Dependency audit evidence

`scripts/Invoke-DependencyAudit.ps1 -Mode Required` fails closed unless direct and transitive advisory data is
available and every project reports no vulnerable packages from `https://api.nuget.org/v3/index.json`. Full validation
runs this audit on all hosted validation platforms. The audit's `dotnet list package --vulnerable --include-transitive`
child command has a 120-second deadline, captures both output streams, and runs inside the shared bounded external-command
runner. Windows and Linux use a Job Object or cgroup v2/child-subreaper process-tree proof to claim whole-tree
containment, and fail closed when containment, capture, inspection, termination, cleanup, or exit status cannot be
proven. macOS uses a session/process-group boundary plus a bounded descendant walk; its result names that boundary
and states that a process deliberately creating a new session can escape. Windows uses a kill-on-close Job Object
without the breakaway creation flag and treats Job Object queries as required evidence. On Linux without a writable
cgroup v2 hierarchy, the wrapper enables `PR_SET_CHILD_SUBREAPER`, keeps the wrapper alive until the target exit and
descendant inspection settle, and inspects the same-PID-namespace `/proc` parent tree; failure to enable the subreaper,
inspect the tree, terminate descendants, or drain the wrapper fails closed. Other Unix platforms without a supported
boundary do not report successful gates.
A timeout names the blocked operation and fails closed. All validation-script gate commands, including package smoke,
inspection, restore, the final consumer, the sample run, and the direct integration endpoints, use the same runner;
stdout is printed before stderr, so the runner does not promise cross-stream chronology. Full validation also applies
per-step watchdogs:
60 seconds for repository guards, 180 seconds for restore, 300 seconds for formatting/build/sample, 600 seconds for
each test project, 600 seconds for package smoke, and 180 seconds for the audit step. Audit evidence is valid only
for the exact candidate and hosted run that produced it, so use the current candidate's CI conclusion rather than a
dated statement in this document.
Treat a non-zero result as a release blocker. No release action is authorized by a passing audit.

## Release workflow command boundary

`build/Test-ExternalCommandWorkflow.ps1` is the contract guard for both `.github/workflows/validate.yml` and
`.github/workflows/release.yml`. The publish job checks out `${{ github.sha }}` with `contents: read` before downloading
the validated package artifacts, so its scripts, bounded runner, routing controls, and executable shims are present in
the separate publish workspace. The guard parses `run` block scalars and shell command structure, including call
operators, separators, pipelines, conditionals/loops, `Start-Process`, and nested PowerShell commands. Workflow YAML
may select actions and invoke approved repository PowerShell/Bash entry scripts, but it must not run direct `git`,
`dotnet`, or shell validation commands. `scripts/Invoke-ReleaseWorkflow.ps1` is the
release gate entry point: provenance fetch/revision checks, restore, format, build, test, integration, pack,
normalization, package inspection, isolated consumer smoke, dependency audit, and NuGet publication all execute
through `build/Invoke-ExternalCommand.ps1` and its runtime shims. Workflow/job/step timeouts remain secondary limits. The publish action's
OIDC authentication is an action boundary; only the resulting package push is an external command and it is routed
through the runner.

`build/Test-NestedPwshLaunch.ps1` is the shared child-launch guard. It checks each PowerShell invocation individually,
including PowerShell and `Start-Process` commands embedded in here-strings, and scans C# `ProcessStartInfo`/
`Process.Start` sites. C# launches must use an inspectable `ProcessStartInfo` with shell execution disabled, both
output streams redirected, and window creation disabled; the guard fails closed if its C# scan is vacuous or finds a
launch without those settings.

## Release preparation

Before a release: promote accepted `PublicAPI.Unshipped.txt` entries into `PublicAPI.Shipped.txt`, finalize the
`CHANGELOG.md` entry for the target version, and re-run the full gate on the resulting commit. Tagging, publishing,
and repository visibility changes are separate, explicitly approved steps.

The tag-time release backstop is `scripts/Validate-ReleaseContract.ps1`. The release workflow also requires the tag
commit to equal the refreshed `origin/main` revision through `scripts/Validate-ReleaseProvenance.ps1`, then runs
formatting, Release build/tests, both verified integration endpoints, package smoke, and the required dependency audit
before either publication step. A finalized changelog must retain exactly one `## [Unreleased]` heading with no
non-whitespace content beneath it; release notes belong under the dated target version. The release-facing hygiene
guard scans every tracked authored text/source file, including the guard scripts and their tests, without filename or
extension exemptions; the sole declared PNG follows the bounded binary path above.
