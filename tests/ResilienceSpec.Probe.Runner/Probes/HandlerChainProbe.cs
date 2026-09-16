using System.Net;

namespace ResilienceSpec.Probe.Runner;

/// <summary>
/// Section 26 item 1: proves the scripted terminal handler really sits behind the standard resilience handler,
/// with a negative control that removes the resilience handler from an otherwise identical chain.
/// </summary>
internal static class HandlerChainProbe
{
    public static async Task RunAsync(ProbeReport report, CancellationToken cancellationToken)
    {
        report.Section("Probe 1 - handler-chain fidelity (section 26 item 1)");
        report.Note("both chains use the same 503 then 200 script and the same in-memory terminal handler");

        await MeasureAsync(report, "with the standard resilience handler", withResilience: true, cancellationToken).ConfigureAwait(false);
        await MeasureAsync(report, "negative control: resilience handler removed", withResilience: false, cancellationToken).ConfigureAwait(false);
    }

    private static async Task MeasureAsync(ProbeReport report, string label, bool withResilience, CancellationToken cancellationToken)
    {
        var script = new[] { ScriptStep.Response(HttpStatusCode.ServiceUnavailable), ScriptStep.Success() };
        using var chain = withResilience
            ? Chains.Standard("probe-chain", script)
            : Chains.WithoutResilience("probe-chain", script);

        report.Note(label);
        using var response = await chain.SendAsync(HttpMethod.Get, cancellationToken).ConfigureAwait(false);

        report.Fact($"{label}: final status returned to the caller", (int)response.StatusCode);
        report.Fact($"{label}: attempts that reached the terminal handler", chain.Terminal.AttemptCount);
        report.Items(
            $"{label}: attempts recorded at the terminal handler",
            chain.Terminal.Records.Select(record =>
                $"attempt {record.Ordinal}, method {record.Method}, observer {record.Observer}, outcome {record.ScriptedOutcome}, at {ProbeReport.Format(record.SinceStart)}"));
        report.Items($"{label}: handler observations in order", chain.Timeline.Render());
        report.Fact($"{label}: requests entering the outermost recorder", CountRequests(chain, Chains.OuterObserver));
        report.Fact($"{label}: requests entering the recorder below the resilience handler", CountRequests(chain, Chains.InnerObserver));

        if (withResilience)
        {
            report.Expect(chain.Terminal.AttemptCount == 2, "the standard resilience handler produces two terminal attempts for 503 then 200");
            report.Expect((int)response.StatusCode == 200, "the caller receives the successful second attempt response");
            report.Expect(
                CountRequests(chain, Chains.OuterObserver) == 1 && CountRequests(chain, Chains.InnerObserver) == 2,
                "the recorder above the resilience handler sees one call while the recorder below it sees both attempts");
            var first = chain.Timeline.Snapshot()[0];
            report.Expect(
                string.Equals(first.Observer, Chains.OuterObserver, StringComparison.Ordinal),
                "the outer recorder is upstream of the resilience handler in the assembled chain");
        }
        else
        {
            report.Expect(chain.Terminal.AttemptCount == 1, "without the resilience handler the same script produces exactly one attempt");
            report.Expect((int)response.StatusCode == 503, "without the resilience handler the caller receives the scripted 503");
        }
    }

    private static int CountRequests(ProbeChain chain, string observer) =>
        chain.Timeline.Snapshot().Count(entry =>
            string.Equals(entry.Observer, observer, StringComparison.Ordinal) &&
            string.Equals(entry.Detail, "request", StringComparison.Ordinal));
}
