# KeelMatrix.ResilienceSpec Compatibility

This document explains the repository's first-party integration compatibility contract. The machine-readable source of
truth is [`build/ResilienceCompatibility.props`](../build/ResilienceCompatibility.props); repository validation checks
that the integration matrix and public documentation repeat it exactly.

The verified Microsoft.Extensions.Http.Resilience versions are **9.8.0** and **10.10.0**. Other versions are unverified.

Both versions are exercised through the real standard handler chain. The package-consumer smoke uses the default
10.10.0 endpoint, while the validation workflow and direct integration commands run both endpoints. The
implementation-neutral script, report, and assertion core does not reference Microsoft.Extensions.Http.Resilience or
Polly. The shipping package intentionally references Microsoft.Extensions.Http for its `IHttpClientFactory` adapter.
