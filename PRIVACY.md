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
scripted status or canonical integer delta-seconds `Retry-After` value, and injected-clock timing. Scripted responses
carry an empty body and no headers other than the scripted `Retry-After` value. Scripts are bounded to
`HttpFaultScript.MaximumSteps` steps and
the recorded attempt timeline keeps at most `HttpAttemptReport.MaximumRecordedAttempts` attempts, so a client that
makes more attempts than that cannot grow attempt state inside the test process: the run reports
`HttpAttemptReport.IsOverflowed` and attempt-state assertions fail with `AttemptStateOverflowException` instead of
reporting a truncated total. Result-level assertions remain evaluable because the caller-visible outcome is exact. When
the report is overflowed, `LastAttempt` is the last recorded attempt, not necessarily the final served attempt. A very
long runaway retry loop may remain `Pending` when the observation window ends; the served count and overflow state are
still explicit.

Injected timing remains local evidence: a known timer deadline at or below the remaining virtual budget is targeted
directly, while a deadline beyond that budget remains an honest cutoff. No wall-clock tolerance or request-identifying
data is added to timing reports.

## Optional telemetry

ResilienceSpec requests the shared activation and heartbeat only when all of these product conditions hold:

- the scenario has genuinely settled; an observation cutoff or pending result is not settlement;
- an executed attempt produced an HTTP error response (status 400 or higher) or a network failure, or a supported timeout outcome is positively tied to an executed attempt; and
- at least one resilience assertion is evaluated after settlement.

Plain caller cancellation, arbitrary upstream timeout exceptions, failures in unexecuted script steps, and observation
cleanup do not qualify. This is the product-specific eligibility rule; ResilienceSpec sends no scenario, report, request,
or assertion data. It calls the shared client's no-argument `TrackActivation()` and `TrackHeartbeat()` methods.

The shared `KeelMatrix.Telemetry` client owns event payload, duplicate suppression, heartbeat cadence, opt-out handling,
anonymous identity, state, queueing, delivery, and failure behavior. ResilienceSpec has no local parser or telemetry
state. See the [shared package README](https://github.com/KeelMatrix/Telemetry/blob/main/app/src/KeelMatrix.Telemetry/README.md)
and [shared privacy policy](https://github.com/KeelMatrix/Telemetry/blob/main/app/PRIVACY.md) for those details.

When enabled, the shared client may make an outbound network request. The in-memory scenario verification itself
requires no network. KeelMatrix tests, samples, package smoke, and validation suppress telemetry with
`KEELMATRIX_NO_TELEMETRY=1`; that evidence covers the verification path, not the optional telemetry transport.
