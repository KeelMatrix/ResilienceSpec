# Contributing

Contributions are welcome as focused fixes, tests, and documentation improvements.

## Before you begin

Use a clean clone and keep generated output, credentials, customer data, and machine-specific paths out of commits.
Set `KEELMATRIX_NO_TELEMETRY=1` during local validation so development activity is not counted as external demand.

## Make changes

Public API changes are recorded in the analyzer baseline files next to the package project
(`src/KeelMatrix.ResilienceSpec/PublicAPI.*.txt`). Add new API to `PublicAPI.Unshipped.txt` during development and
promote accepted entries into `PublicAPI.Shipped.txt` when a release is prepared; a Release build fails when the
baseline and the public surface disagree.

Behaviour changes should come with tests that prove the contract:

- core script, reporting, assertion, privacy, and cancellation behaviour in `tests/KeelMatrix.ResilienceSpec.Tests`;
- composition with Microsoft's resilience handlers in `tests/KeelMatrix.ResilienceSpec.IntegrationTests`;
- documented quick-start behaviour in `samples/KeelMatrix.ResilienceSpec.Sample`.

Documentation changes must keep `README.md`, the package README, the XML documentation, and `CHANGELOG.md`
consistent with shipped behaviour.

## Validation

Run the repository gate from the repository root:

```powershell
pwsh -NoProfile -File .\scripts\Validate.ps1
```

`-Mode Focused` skips the formatting gate, `-SkipPackage` skips the pack, inspection, and clean-consumer smoke, and
`-Mode Full` adds the dependency vulnerability audit. Use `-ResilienceVersion 9.8.0` to run the integration suite
against the lowest supported Microsoft resilience release.

See [docs/DEV.md](docs/DEV.md) for the individual commands and what each gate proves.

Before creating a release tag, run the repository's single release-contract check against the finalized changelog:

```powershell
pwsh -NoProfile -File .\scripts\Validate-ReleaseContract.ps1 -Tag v0.1.0
```

The check must pass only after the target entry is dated, version-consistent, and written as an `Added`-only first
release entry. The tag-triggered workflow runs the same check again.

## Security and community

Security reports must use the private channels in [`SECURITY.md`](SECURITY.md), not a public issue. Community conduct
concerns should follow [`CODE_OF_CONDUCT.md`](CODE_OF_CONDUCT.md), not the vulnerability-reporting route.
