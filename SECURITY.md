# Security Policy

## Reporting a Vulnerability

Report suspected vulnerabilities privately before any public disclosure:

1. Email **keelmatrix@gmail.com**, which is the private reporting route for this repository.
2. If private vulnerability reporting is enabled in GitHub for this repository, you may also open a private GitHub
   Security Advisory.

Do not create a public issue or publicly disclose vulnerability details, exploit steps, credentials, request data,
attempt records, or scripted scenarios that contain sensitive values.

Include, when safe:

- the affected package version, target framework, and operating system;
- a minimal reproduction, ideally a scripted scenario that shows the unexpected attempt, timing, or outcome;
- the security impact and the affected trust boundary, including whether request data could be observed or leaked;
- sanitized diagnostics and a suggested mitigation, if known.

We aim to acknowledge reports within five business days and will follow up as the assessment proceeds.

Routine bug reports and usage questions belong in the project's normal public channels and do not need the private
security route, unless they may involve a vulnerability.

## Scope

This policy covers the `KeelMatrix.ResilienceSpec` package, its scripted terminal handler, its attempt records and
diagnostics, its assertions, its HttpClientFactory adapter, its telemetry integration, and the repository's package
artifacts. Vulnerabilities in the resilience library or `HttpClient` under test should be reported to the relevant
maintainer, but may be included when they expose a ResilienceSpec boundary.

## Supported Versions

Security fixes are prioritized for the latest maintained release line and its supported `net8.0` runtime
environments. Older versions and unsupported runtimes may receive fixes on a case-by-case basis.
