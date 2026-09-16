using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Http.Resilience;

namespace ResilienceSpec.Probe.Runner;

/// <summary>Section 26 item 2: the true attempt count and outcome for a 503 then 200 script.</summary>
internal static class AttemptTruthProbe
{
    public static async Task RunAsync(ProbeReport report, CancellationToken cancellationToken)
    {
        report.Section("Probe 2 - 503 then 200 attempt truth (section 26 item 2)");
        ReportInstalledDefaults(report);

        using (var chain = Chains.Standard(
            "probe-truth",
            new[] { ScriptStep.Response(HttpStatusCode.ServiceUnavailable), ScriptStep.Success() }))
        {
            var clock = Stopwatch.StartNew();
            using var response = await chain.SendAsync(HttpMethod.Get, cancellationToken).ConfigureAwait(false);
            clock.Stop();

            report.Fact("attempts that reached the terminal handler", chain.Terminal.AttemptCount);
            report.Fact("final status returned to the caller", (int)response.StatusCode);
            report.Fact("attempt outcomes at the terminal handler", string.Join(" -> ", chain.Terminal.Records.Select(record => record.ScriptedOutcome)));
            report.Fact("inter-attempt gap (real clock, reference only)", MeasuredGap(chain));
            report.Fact("total wall-clock time (real clock, reference only)", ProbeReport.Format(clock.Elapsed));
            report.Fact("second terminal attempt occurred", chain.Terminal.AttemptCount > 1);

            report.Expect(chain.Terminal.AttemptCount == 2, "the 503 then 200 script is observed as exactly two attempts at the terminal handler");
            report.Expect((int)response.StatusCode == 200, "the final status returned to the caller is 200");
            report.Expect(chain.Terminal.Records[0].ScriptedOutcome.StartsWith("response 503", StringComparison.Ordinal), "the first attempt carries the scripted 503");
        }

        using (var chain = Chains.Standard(
            "probe-retry-ceiling",
            Enumerable.Range(0, 8).Select(_ => ScriptStep.Response(HttpStatusCode.ServiceUnavailable))))
        {
            var clock = Stopwatch.StartNew();
            using var response = await chain.SendAsync(HttpMethod.Get, cancellationToken).ConfigureAwait(false);
            clock.Stop();

            report.Fact("attempts for an always-503 script (default options)", chain.Terminal.AttemptCount);
            report.Fact("default retry attempts above the first attempt", chain.Terminal.AttemptCount - 1);
            report.Fact("final status for an always-503 script", (int)response.StatusCode);
            report.Fact("total wall-clock time for the always-503 script (real clock, reference only)", ProbeReport.Format(clock.Elapsed));

            report.Expect(chain.Terminal.AttemptCount == 4, "the default configuration permits three retries above the first attempt");
            report.Expect((int)response.StatusCode == 503, "the caller receives the last scripted status once retries are exhausted");
        }
    }

    private static void ReportInstalledDefaults(ProbeReport report)
    {
        var defaults = new HttpStandardResilienceOptions();
        report.Fact("installed default retry maximum attempts", defaults.Retry.MaxRetryAttempts);
        report.Fact("installed default retry delay", ProbeReport.Format(defaults.Retry.Delay));
        report.Fact("installed default retry backoff type", defaults.Retry.BackoffType);
        report.Fact("installed default retry jitter", defaults.Retry.UseJitter);
        report.Fact("installed default retry-after header handling", defaults.Retry.ShouldRetryAfterHeader);
        report.Fact("installed default attempt timeout", ProbeReport.Format(defaults.AttemptTimeout.Timeout));
        report.Fact("installed default total request timeout", ProbeReport.Format(defaults.TotalRequestTimeout.Timeout));
        report.Fact("installed default circuit-breaker minimum throughput", defaults.CircuitBreaker.MinimumThroughput);
    }

    private static string MeasuredGap(ProbeChain chain)
    {
        var records = chain.Terminal.Records;
        return records.Count < 2
            ? "no retry observed"
            : ProbeReport.Format(records[1].SinceStart - records[0].SinceStart);
    }
}
