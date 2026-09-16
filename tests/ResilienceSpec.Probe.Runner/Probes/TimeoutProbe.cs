using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Time.Testing;
using Polly;

namespace ResilienceSpec.Probe.Runner;

/// <summary>
/// Section 26 item 4 d: whether the per-attempt timeout and the total request timeout can be driven by the
/// same controllable time source as the retry delay.
/// </summary>
internal static class TimeoutProbe
{
    private static readonly DateTimeOffset ControlledStart = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    public static async Task RunAsync(ProbeReport report, CancellationToken cancellationToken)
    {
        report.Section("Probe 4d - per-attempt timeout and total request timeout");
        report.Fact("configured per-attempt timeout", ProbeReport.Format(AttemptTimeout));
        report.Fact("configured total request timeout", ProbeReport.Format(TotalTimeout));
        report.Fact("configured retry delay", ProbeReport.Format(RetryDelay));

        var standardHandlerIsControlled = await MeasureAsync(
            report,
            "standard handler with a registered TimeProvider",
            UseStandardHandler,
            cancellationToken).ConfigureAwait(false);

        var controlledPipelineIsControlled = await MeasureAsync(
            report,
            "pipeline assembled through the public builder",
            UseControlledPipeline,
            cancellationToken).ConfigureAwait(false);

        report.Verdict(
            "4d timeouts",
            controlledPipelineIsControlled ? GateVerdict.Pass : GateVerdict.Fail,
            controlledPipelineIsControlled
                ? "per-attempt and total timeouts fire on the injected clock when the pipeline is assembled through the public builder time hook; " +
                    $"the standard handler with a registered TimeProvider is controllable: {(standardHandlerIsControlled ? "yes" : "no")}"
                : "neither timeout could be driven deterministically by the injected clock");
    }

    private static async Task<bool> MeasureAsync(
        ProbeReport report,
        string label,
        Func<FakeTimeProvider, ProbeChain> build,
        CancellationToken cancellationToken)
    {
        var fake = new FakeTimeProvider(ControlledStart);
        using var chain = build(fake);

        var steps = new[] { AttemptTimeout, RetryDelay, AttemptTimeout, RetryDelay };
        var measurement = await ClockMeasurements.MeasureAsync(new ClockMeasurementRequest(
            chain,
            Window,
            fake.Advance,
            steps,
            TotalTimeout + TimeSpan.FromSeconds(3),
            cancellationToken)).ConfigureAwait(false);

        ClockMeasurements.Report(report, label, measurement);

        report.Expect(measurement.AttemptsImmediately == 1, $"{label}: the first attempt is still pending before any clock advance");
        report.Expect(measurement.Settled, $"{label}: the request settles instead of hanging");

        if (measurement.CompletedAfterAdvance)
        {
            report.Expect(
                measurement.Outcome.Contains("TimeoutRejectedException", StringComparison.Ordinal),
                $"{label}: the request ends with a strategy timeout");
        }

        return !measurement.CompletedImmediately && measurement.CompletedAfterAdvance;
    }

    private static ProbeChain UseStandardHandler(FakeTimeProvider fake) =>
        Chains.Standard(
            "probe-timeout-standard",
            HangingScript(),
            options =>
            {
                options.AttemptTimeout.Timeout = AttemptTimeout;
                options.TotalRequestTimeout.Timeout = TotalTimeout;
                VirtualTimeProbe.ConfigureControlledRetry(options.Retry, RetryDelay);
            },
            services => services.AddSingleton<TimeProvider>(fake));

    private static ProbeChain UseControlledPipeline(FakeTimeProvider fake) =>
        Chains.Controlled(
            "probe-timeout-controlled",
            HangingScript(),
            fake,
            pipeline =>
            {
                pipeline.AddTimeout(new HttpTimeoutStrategyOptions { Timeout = TotalTimeout });
                pipeline.AddRetry(new HttpRetryStrategyOptions
                {
                    MaxRetryAttempts = 1,
                    Delay = RetryDelay,
                    BackoffType = DelayBackoffType.Constant,
                    UseJitter = false,
                });
                pipeline.AddTimeout(new HttpTimeoutStrategyOptions { Timeout = AttemptTimeout });
            });

    private static IEnumerable<ScriptStep> HangingScript() =>
        Enumerable.Range(0, 5).Select(_ => ScriptStep.Hang()).Append(ScriptStep.Success());
}
