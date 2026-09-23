# Privacy

KeelMatrix.ResilienceSpec keeps scenarios, attempt reports, and diagnostics local. It has no hosted verification
backend and never sends request or response data anywhere. The verification path needs no listener, socket, DNS
lookup, container, or hosted service; the optional telemetry described below is the only network behaviour the
package can trigger.

## Platforms

The package targets `net8.0`. Repository validation disables telemetry on every supported hosted platform, so
KeelMatrix's own tests, package smoke, and sample runs are not counted as product demand. The current workflow topology
is maintained in [`docs/DEV.md`](docs/DEV.md).

## Product data boundary

The scripted downstream runs in memory inside your test process. The package does not transmit request URIs, hosts,
paths, query strings, headers, cookies, authorization values, request or response bodies, exception messages, client
or service names, scripted scenario contents, repository names, or local paths.

Attempt records and failure messages contain only the attempt ordinal, the HTTP method, the broad outcome, the
scripted status or `Retry-After` value, and injected-clock timing. Scripted responses carry an empty body and no
headers other than the scripted `Retry-After` value. Scripts are bounded to `HttpFaultScript.MaximumSteps` steps and
the recorded attempt timeline keeps at most `HttpAttemptReport.MaximumRecordedAttempts` attempts, so a client that
makes more attempts than that cannot grow attempt state inside the test process: the run reports
`HttpAttemptReport.IsOverflowed` and attempt-state assertions fail with `AttemptStateOverflowException` instead of
reporting a truncated total. Result-level assertions remain evaluable because the caller-visible outcome is exact. When
the report is overflowed, `LastAttempt` is the last recorded attempt, not necessarily the final served attempt. A very
long runaway retry loop may remain `Pending` when the observation window ends; the served count and overflow state are
still explicit.

## Optional telemetry

The optional `KeelMatrix.Telemetry` integration requests only the shared privacy-preserving activation and weekly
heartbeat contract, which uses pseudonymous identifiers rather than request or scenario content. An activation is
requested when a scripted scenario reached at least one injected failure **and** at least one resilience assertion
was evaluated. Constructing a script, handler, or scenario, running a scenario that only succeeds, and failing to
evaluate any assertion do not activate telemetry. Identifier, storage, retention, and delivery details belong to the
maintained shared policy linked below.

Telemetry is best-effort and opt-out. A telemetry failure cannot change a scenario result, fail a test, or affect the
host application.

Telemetry is the one part of the package that is network behaviour: when it is enabled, an activation is posted over
HTTPS to the shared KeelMatrix telemetry endpoint, which resolves a name and opens a socket. That is why the
verification path stays offline only while telemetry is disabled. KeelMatrix development and validation runs always
set `KEELMATRIX_NO_TELEMETRY=1`, and any consumer that needs a fully offline test process sets it too.

## Local state and controls

ResilienceSpec does not persist scenarios, attempt reports, or diagnostics. The shared telemetry dependency may create
local marker or queue files to support delivery and opt-out; its maintained policy documents those details.

Set `KEELMATRIX_NO_TELEMETRY=1` for local or CI validation. KeelMatrix development and validation runs suppress
telemetry and are not demand measurements, and the repository's zero-socket package evidence is produced under that
same setting, so it covers the verification path only. The shared telemetry package also honours its documented
process-level and repository-local opt-out controls.

## Shared telemetry policy

See the [KeelMatrix.Telemetry privacy policy](https://github.com/KeelMatrix/Telemetry/blob/main/PRIVACY.md) for
storage, retention, identifier, and opt-out details.
