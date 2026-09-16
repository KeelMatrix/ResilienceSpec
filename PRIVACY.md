# Privacy

KeelMatrix.ResilienceSpec keeps scenarios, attempt reports, and diagnostics local. It has no hosted verification
backend and never sends request or response data anywhere.

## Product data boundary

The scripted downstream runs in memory inside your test process. The package does not transmit request URIs, hosts,
paths, query strings, headers, cookies, authorization values, request or response bodies, exception messages, client
or service names, scripted scenario contents, repository names, or local paths.

Attempt records and failure messages contain only the attempt ordinal, the HTTP method, the broad outcome, the
scripted status or `Retry-After` value, and injected-clock timing. Scripted responses carry an empty body and no
headers other than the scripted `Retry-After` value, and scripts and timelines are bounded to a maximum step count.

## Optional telemetry

The optional `KeelMatrix.Telemetry` integration requests only the shared anonymous activation and weekly heartbeat
contract. An activation is requested when a scripted scenario reached at least one injected failure **and** at least
one resilience assertion was evaluated. Constructing a script, handler, or scenario, running a scenario that only
succeeds, and failing to evaluate any assertion do not activate telemetry.

Telemetry is best-effort and opt-out. A telemetry failure cannot change a scenario result, fail a test, or affect the
host application.

## Local state and controls

ResilienceSpec does not persist scenarios, attempt reports, or diagnostics. The shared telemetry dependency may create
local marker or queue files to support delivery and opt-out; its maintained policy documents those details.

Set `KEELMATRIX_NO_TELEMETRY=1` for local or CI validation. KeelMatrix development and validation runs suppress
telemetry and are not demand measurements. The shared telemetry package also honours its documented process-level and
repository-local opt-out controls.

## Shared telemetry policy

See the [KeelMatrix.Telemetry privacy policy](https://github.com/KeelMatrix/Telemetry/blob/main/PRIVACY.md) for
storage, retention, identifier, and opt-out details.
