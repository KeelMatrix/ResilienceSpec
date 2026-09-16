# Samples

`KeelMatrix.ResilienceSpec.Sample` is a small console walkthrough of the documented quick start. It configures a real
`IHttpClientFactory` client with Microsoft's standard resilience handler, replaces only the terminal network boundary
with a scripted downstream, and prints the assertions it evaluates.

Run it from the repository root:

```text
dotnet run --project samples/KeelMatrix.ResilienceSpec.Sample -c Release
```

Expected output is one `PASS` line per scenario followed by `Sample completed: every documented expectation held.`
The sample exits with a non-zero code when an expectation does not hold.

The sample references the library project directly so it can run from a source checkout without a local package feed.
The packed package is exercised separately by `scripts/Invoke-PackageSmoke.ps1`, which restores the built `.nupkg`
from an isolated local feed and runs `tests/PackageSmoke` against it.
