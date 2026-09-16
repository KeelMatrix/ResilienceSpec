using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Time.Testing;
using Polly;

namespace ResilienceSpec.Probe.Runner;

/// <summary>
/// Section 26 item 4 a and b: whether retry delays can be driven by a controllable clock through the
/// dependency-injection container and through the public resilience pipeline builder.
/// </summary>
internal static class VirtualTimeProbe
{
    private static readonly DateTimeOffset ControlledStart = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan ControlledRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    public static async Task RunAsync(ProbeReport report, CancellationToken cancellationToken)
    {
        report.Section("Probe 4a - DI TimeProvider registered beside the standard resilience handler");
        await MeasureServiceProviderTimeProviderAsync(report, cancellationToken).ConfigureAwait(false);
        await MeasureSystemClockControlAsync(report, cancellationToken).ConfigureAwait(false);

        report.Section("Probe 4b - public builder time hook (pipeline the probe assembles)");
        await MeasureBuilderTimeProviderAsync(report, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Control for 4a: exactly the same chain and the same scripted delay, with no TimeProvider registered.
    /// The retry must then wait on the system clock, which shows that the result above comes from registration.
    /// </summary>
    private static async Task MeasureSystemClockControlAsync(ProbeReport report, CancellationToken cancellationToken)
    {
        using var chain = Chains.Standard(
            "probe-system-time",
            new[] { ScriptStep.Response(HttpStatusCode.ServiceUnavailable), ScriptStep.Success() },
            options => ConfigureControlledRetry(options.Retry, ControlledRetryDelay));

        var steps = new[] { ControlledRetryDelay + TimeSpan.FromSeconds(1) };
        var measurement = await ClockMeasurements.MeasureAsync(new ClockMeasurementRequest(
            chain,
            Window,
            _ => { },
            steps,
            ControlledRetryDelay + TimeSpan.FromSeconds(2),
            cancellationToken)).ConfigureAwait(false);

        ClockMeasurements.Report(report, "control: no TimeProvider registered", measurement);
        report.Expect(
            measurement.CompletedOnSystemClock,
            "without a registered TimeProvider the same scripted delay completes on the system clock");
    }

    private static async Task MeasureServiceProviderTimeProviderAsync(ProbeReport report, CancellationToken cancellationToken)
    {
        var fake = new FakeTimeProvider(ControlledStart);
        using var chain = Chains.Standard(
            "probe-di-time",
            new[] { ScriptStep.Response(HttpStatusCode.ServiceUnavailable), ScriptStep.Success() },
            options => ConfigureControlledRetry(options.Retry, ControlledRetryDelay),
            services => services.AddSingleton<TimeProvider>(fake));

        report.Fact("registered TimeProvider implementation", fake.GetType().FullName);
        report.Fact("configured retry delay", ProbeReport.Format(ControlledRetryDelay));
        report.Fact("configured retry backoff", "constant, jitter off");
        report.Fact("configured retry maximum attempts", 1);

        var steps = new[] { ControlledRetryDelay + TimeSpan.FromSeconds(1) };
        var measurement = await ClockMeasurements.MeasureAsync(new ClockMeasurementRequest(
            chain,
            Window,
            fake.Advance,
            steps,
            ControlledRetryDelay + TimeSpan.FromSeconds(2),
            cancellationToken)).ConfigureAwait(false);

        ClockMeasurements.Report(report, "DI TimeProvider", measurement);

        if (!measurement.CompletedImmediately && measurement.CompletedAfterAdvance)
        {
            report.Verdict(
                "4a DI TimeProvider",
                GateVerdict.Pass,
                "the standard handler's retry delay resumed only after the injected FakeTimeProvider advanced");
            return;
        }

        if (measurement.CompletedOnSystemClock)
        {
            report.Verdict(
                "4a DI TimeProvider",
                GateVerdict.Fail,
                $"the standard handler ignored the injected TimeProvider and resumed on the system clock after {ProbeReport.Format(measurement.WallElapsed)}");
            return;
        }

        report.Verdict(
            "4a DI TimeProvider",
            GateVerdict.Narrow,
            "inconclusive: the retry delay neither resumed on the injected clock nor completed on the system clock inside the observation windows");
    }

    private static async Task MeasureBuilderTimeProviderAsync(ProbeReport report, CancellationToken cancellationToken)
    {
        var fake = new FakeTimeProvider(ControlledStart);
        using var chain = Chains.Controlled(
            "probe-builder-time",
            new[] { ScriptStep.Response(HttpStatusCode.ServiceUnavailable), ScriptStep.Success() },
            fake,
            pipeline => pipeline.AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = 1,
                Delay = ControlledRetryDelay,
                BackoffType = DelayBackoffType.Constant,
                UseJitter = false,
            }));

        report.Fact("time source assigned to the pipeline builder", $"{fake.GetType().FullName} via ResiliencePipelineBuilder.TimeProvider");
        report.Fact("configured retry delay", ProbeReport.Format(ControlledRetryDelay));

        var steps = new[] { ControlledRetryDelay - TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1) + TimeSpan.FromMilliseconds(10) };
        var measurement = await ClockMeasurements.MeasureAsync(new ClockMeasurementRequest(
            chain,
            Window,
            fake.Advance,
            steps,
            ControlledRetryDelay + TimeSpan.FromSeconds(2),
            cancellationToken)).ConfigureAwait(false);

        ClockMeasurements.Report(report, "builder time hook", measurement);

        var resumedOnlyWhenTheFullDelayElapsed =
            !measurement.CompletedImmediately &&
            measurement.CompletedAfterAdvance &&
            measurement.AdvancesApplied == 2 &&
            measurement.FinalAttempts == 2;

        report.Expect(
            resumedOnlyWhenTheFullDelayElapsed,
            "the controlled pipeline resumes the retry only after the configured delay elapses on the injected clock");

        report.Verdict(
            "4b builder time hook",
            resumedOnlyWhenTheFullDelayElapsed ? GateVerdict.Pass : GateVerdict.Narrow,
            resumedOnlyWhenTheFullDelayElapsed
                ? "ResiliencePipelineBuilder.TimeProvider controls the retry delay through the public AddResilienceHandler API"
                : "the builder hook did not produce the expected controllable delay; see the measurements above");
    }

    internal static void ConfigureControlledRetry(HttpRetryStrategyOptions options, TimeSpan delay)
    {
        options.MaxRetryAttempts = 1;
        options.Delay = delay;
        options.BackoffType = DelayBackoffType.Constant;
        options.UseJitter = false;
    }
}
