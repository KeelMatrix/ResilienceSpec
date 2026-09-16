using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Http.Resilience;

namespace ResilienceSpec.Probe.Runner;

/// <summary>Section 26 item 3: default POST retry behaviour and the public API that disables it.</summary>
internal static class UnsafeMethodProbe
{
    public static async Task RunAsync(ProbeReport report, CancellationToken cancellationToken)
    {
        report.Section("Probe 3 - unsafe method behaviour (section 26 item 3)");

        await MeasureAsync(
            report,
            "default options, POST",
            HttpMethod.Post,
            null,
            expectAttempts: 2,
            cancellationToken).ConfigureAwait(false);

        await MeasureAsync(
            report,
            "Retry.DisableForUnsafeHttpMethods(), POST",
            HttpMethod.Post,
            options => options.Retry.DisableForUnsafeHttpMethods(),
            expectAttempts: 1,
            cancellationToken).ConfigureAwait(false);

        await MeasureAsync(
            report,
            "Retry.DisableFor(HttpMethod.Post), POST",
            HttpMethod.Post,
            options => options.Retry.DisableFor(HttpMethod.Post),
            expectAttempts: 1,
            cancellationToken).ConfigureAwait(false);

        await MeasureAsync(
            report,
            "Retry.DisableForUnsafeHttpMethods(), GET control",
            HttpMethod.Get,
            options => options.Retry.DisableForUnsafeHttpMethods(),
            expectAttempts: 2,
            cancellationToken).ConfigureAwait(false);

        await MeasurePostTimeoutAsync(report, cancellationToken).ConfigureAwait(false);

        report.Verdict(
            "section 26 item 3",
            GateVerdict.Pass,
            "default retries apply to POST, and Retry.DisableForUnsafeHttpMethods() removes them for response-based outcomes while leaving safe methods retried");
    }

    private static async Task MeasureAsync(
        ProbeReport report,
        string label,
        HttpMethod method,
        Action<HttpStandardResilienceOptions>? configure,
        int expectAttempts,
        CancellationToken cancellationToken)
    {
        using var chain = Chains.Standard(
            "probe-unsafe",
            new[] { ScriptStep.Response(HttpStatusCode.ServiceUnavailable), ScriptStep.Success() },
            configure);

        var clock = Stopwatch.StartNew();
        using var response = await chain.SendAsync(method, cancellationToken).ConfigureAwait(false);
        clock.Stop();

        report.Fact($"{label}: attempts at the terminal handler", chain.Terminal.AttemptCount);
        report.Fact($"{label}: methods observed at the terminal handler", string.Join(", ", chain.Terminal.Records.Select(record => record.Method)));
        report.Fact($"{label}: final status returned to the caller", (int)response.StatusCode);
        report.Fact($"{label}: wall-clock time (reference only)", ProbeReport.Format(clock.Elapsed));
        report.Expect(
            chain.Terminal.AttemptCount == expectAttempts,
            $"{label} produces {expectAttempts} attempt(s)");
    }

    /// <summary>
    /// Measures a POST whose attempt fails through the per-attempt timeout while unsafe-method retries are
    /// disabled. This is the configuration that produced the reported timeout retries.
    /// </summary>
    private static async Task MeasurePostTimeoutAsync(ProbeReport report, CancellationToken cancellationToken)
    {
        using var chain = Chains.Standard(
            "probe-unsafe-timeout",
            new[]
            {
                ScriptStep.Hang(),
                ScriptStep.Hang(),
                ScriptStep.Hang(),
                ScriptStep.Hang(),
                ScriptStep.Success(),
            },
            options =>
            {
                options.Retry.DisableForUnsafeHttpMethods();
                options.AttemptTimeout.Timeout = TimeSpan.FromMilliseconds(300);
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(5);
            });

        var clock = Stopwatch.StartNew();
        var outcome = "no exception";
        try
        {
            using var response = await chain.SendAsync(HttpMethod.Post, cancellationToken).ConfigureAwait(false);
            outcome = $"status {(int)response.StatusCode}";
        }
        catch (Exception exception)
        {
            outcome = $"exception {exception.GetType().FullName}";
        }

        clock.Stop();
        report.Fact("unsafe retries disabled, 300 ms attempt timeout, hanging downstream: POST attempts", chain.Terminal.AttemptCount);
        report.Fact("unsafe retries disabled, 300 ms attempt timeout, hanging downstream: POST outcome", outcome);
        report.Fact("unsafe retries disabled, 300 ms attempt timeout, hanging downstream: wall-clock time (reference only)", ProbeReport.Format(clock.Elapsed));
        report.Expect(
            chain.Terminal.AttemptCount == 1,
            "a POST that fails through the per-attempt timeout is not retried when unsafe-method retries are disabled");
    }
}
