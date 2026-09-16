# Samples

`KeelMatrix.ResilienceSpec.Sample` is a small console walkthrough of the documented quick start. It configures a real
`IHttpClientFactory` client with Microsoft's standard resilience handler, replaces only the terminal network boundary
with a scripted downstream, and prints the assertions it evaluates.

Run it from the repository root. The script packs the shipping project into a temporary, isolated local feed,
restores this sample with package-source mapping, and then runs it:

```powershell
pwsh -NoProfile -File scripts/Run-Sample.ps1
```

Expected output is one `PASS` line per scenario followed by `Sample completed: every documented expectation held.`
The sample exits with a non-zero code when an expectation does not hold.

The sample uses a `PackageReference` to `KeelMatrix.ResilienceSpec`. `scripts/Run-Sample.ps1` maps only that package
ID to its temporary feed; Microsoft, Polly, System, and telemetry dependencies restore from NuGet.org. This keeps
the sample on the same package-consumer path as the built artifact without a source-project reference.
