using System.Diagnostics;
using System.Globalization;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Time.Testing;
using Polly;

namespace ResilienceSpec.Probe.Runner;

/// <summary>
/// Section 26 item 4 c: whether <c>Retry-After</c> delays are honored and whether they follow the same
/// controllable time source as the retry backoff.
/// </summary>
internal static class RetryAfterProbe
{
    private static readonly DateTimeOffset ControlledStart = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan HeaderDelta = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HeaderDateOffset = TimeSpan.FromSeconds(5);

    public static async Task RunAsync(ProbeReport report, CancellationToken cancellationToken)
    {
        var summary = new Summary();

        report.Section("Probe 4c - Retry-After as delta seconds");
        await DeltaOnStandardHandlerAsync(report, cancellationToken).ConfigureAwait(false);
        await DeltaOnStandardHandlerWithControlledClockAsync(report, summary, cancellationToken).ConfigureAwait(false);
        await DeltaOnControlledPipelineAsync(report, summary, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        await DeltaOnControlledPipelineAsync(report, summary, TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);

        report.Section("Probe 4c - Retry-After as an HTTP-date");
        await DateOnStandardHandlerAsync(report, cancellationToken).ConfigureAwait(false);
        await DateOnControlledPipelineAsync(report, summary, fromControlledClock: true, cancellationToken).ConfigureAwait(false);
        await DateOnControlledPipelineAsync(report, summary, fromControlledClock: false, cancellationToken).ConfigureAwait(false);
        await PastDateOnControlledPipelineAsync(report, summary, cancellationToken).ConfigureAwait(false);

        var deltasAreDeterministic =
            summary.DeltaDrivesDelayAboveShorterBackoff &&
            summary.DeltaOverridesLongerConfiguredBackoff &&
            summary.DeltaControllableOnStandardHandler;
        var datesAreDeterministic = !summary.DateDeltaComputedFromWallClock && summary.DateWaitDrivenByInjectedClock;

        report.Verdict(
            "4c Retry-After",
            deltasAreDeterministic && datesAreDeterministic
                ? GateVerdict.Pass
                : deltasAreDeterministic ? GateVerdict.Narrow : GateVerdict.Fail,
            summary.Describe(deltasAreDeterministic, datesAreDeterministic));
    }

    private static async Task DeltaOnStandardHandlerAsync(ProbeReport report, CancellationToken cancellationToken)
    {
        var headerDelta = TimeSpan.FromSeconds(5);
        using var chain = Chains.Standard(
            "probe-retryafter-standard",
            new[]
            {
                ScriptStep.Response(HttpStatusCode.ServiceUnavailable, retryAfterDelta: headerDelta),
                ScriptStep.Success(),
            });

        var clock = Stopwatch.StartNew();
        using var response = await chain.SendAsync(HttpMethod.Get, cancellationToken).ConfigureAwait(false);
        clock.Stop();

        report.Fact("standard handler, delta 5 s with the default 2 s backoff: attempts", chain.Terminal.AttemptCount);
        report.Fact("standard handler, delta 5 s with the default 2 s backoff: inter-attempt gap (real clock, reference only)", Gap(chain));
        report.Fact("standard handler, delta 5 s with the default 2 s backoff: total wall-clock time (reference only)", ProbeReport.Format(clock.Elapsed));
        report.Fact("standard handler, delta 5 s with the default 2 s backoff: final status", (int)response.StatusCode);
        report.Expect(chain.Terminal.AttemptCount == 2, "a Retry-After delta does not stop the default retry");
    }

    private static async Task DeltaOnStandardHandlerWithControlledClockAsync(ProbeReport report, Summary summary, CancellationToken cancellationToken)
    {
        var fake = new FakeTimeProvider(ControlledStart);
        using var chain = Chains.Standard(
            "probe-retryafter-di",
            new[]
            {
                ScriptStep.Response(HttpStatusCode.ServiceUnavailable, retryAfterDelta: HeaderDelta),
                ScriptStep.Success(),
            },
            options => VirtualTimeProbe.ConfigureControlledRetry(options.Retry, TimeSpan.FromSeconds(1)),
            services => services.AddSingleton<TimeProvider>(fake));

        var steps = new[] { HeaderDelta };
        var measurement = await ClockMeasurements.MeasureAsync(new ClockMeasurementRequest(
            chain,
            Window,
            fake.Advance,
            steps,
            HeaderDelta + TimeSpan.FromSeconds(3),
            cancellationToken)).ConfigureAwait(false);

        ClockMeasurements.Report(report, "standard handler with a registered TimeProvider, delta 2 s", measurement);

        summary.DeltaControllableOnStandardHandler = !measurement.CompletedImmediately && measurement.CompletedAfterAdvance;
    }

    private static async Task DeltaOnControlledPipelineAsync(ProbeReport report, Summary summary, TimeSpan backoff, CancellationToken cancellationToken)
    {
        var fake = new FakeTimeProvider(ControlledStart);
        using var chain = Chains.Controlled(
            "probe-retryafter-controlled",
            new[]
            {
                ScriptStep.Response(HttpStatusCode.ServiceUnavailable, retryAfterDelta: HeaderDelta),
                ScriptStep.Success(),
            },
            fake,
            pipeline => pipeline.AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = 1,
                Delay = backoff,
                BackoffType = DelayBackoffType.Constant,
                UseJitter = false,
            }));

        var steps = backoff > HeaderDelta
            ? new[] { HeaderDelta, backoff - HeaderDelta }
            : new[] { backoff, HeaderDelta - backoff };

        var measurement = await ClockMeasurements.MeasureAsync(new ClockMeasurementRequest(
            chain,
            Window,
            fake.Advance,
            steps,
            HeaderDelta + backoff + TimeSpan.FromSeconds(2),
            cancellationToken)).ConfigureAwait(false);

        var label = string.Create(
            CultureInfo.InvariantCulture,
            $"controlled pipeline, backoff {backoff.TotalSeconds:0}s, delta {HeaderDelta.TotalSeconds:0}s");
        ClockMeasurements.Report(report, label, measurement);

        if (backoff < HeaderDelta)
        {
            // The header is larger than the backoff: the retry waits for the header value on the injected clock.
            summary.DeltaDrivesDelayAboveShorterBackoff = !measurement.CompletedImmediately && measurement.CompletedAfterAdvance;
        }
        else
        {
            // The backoff is larger than the header: an immediate retry after the header shows the header wins.
            summary.DeltaOverridesLongerConfiguredBackoff =
                !measurement.CompletedImmediately &&
                measurement.CompletedAfterAdvance &&
                measurement.AdvancesApplied == 1;
        }
    }

    private static async Task DateOnStandardHandlerAsync(ProbeReport report, CancellationToken cancellationToken)
    {
        var date = DateTimeOffset.UtcNow + HeaderDateOffset;
        using var chain = Chains.Standard(
            "probe-retryafter-date-standard",
            new[]
            {
                ScriptStep.Response(HttpStatusCode.ServiceUnavailable, retryAfterDate: date),
                ScriptStep.Success(),
            });

        var clock = Stopwatch.StartNew();
        using var response = await chain.SendAsync(HttpMethod.Get, cancellationToken).ConfigureAwait(false);
        clock.Stop();

        report.Fact("standard handler, HTTP-date 5 s ahead: attempts", chain.Terminal.AttemptCount);
        report.Fact("standard handler, HTTP-date 5 s ahead: inter-attempt gap (real clock, reference only)", Gap(chain));
        report.Fact("standard handler, HTTP-date 5 s ahead: total wall-clock time (reference only)", ProbeReport.Format(clock.Elapsed));
        report.Fact("standard handler, HTTP-date 5 s ahead: final status", (int)response.StatusCode);
        report.Note("compare this gap with the installed default retry delay reported in probe 2");
        report.Expect(chain.Terminal.AttemptCount == 2, "a Retry-After HTTP-date does not stop the default retry");
    }

    private static async Task DateOnControlledPipelineAsync(ProbeReport report, Summary summary, bool fromControlledClock, CancellationToken cancellationToken)
    {
        var fake = new FakeTimeProvider(ControlledStart);
        var date = (fromControlledClock ? fake.GetUtcNow() : TimeProvider.System.GetUtcNow()) + HeaderDateOffset;

        using var chain = Chains.Controlled(
            "probe-retryafter-date-controlled",
            new[]
            {
                ScriptStep.Response(HttpStatusCode.ServiceUnavailable, retryAfterDate: date),
                ScriptStep.Success(),
            },
            fake,
            pipeline => pipeline.AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = 1,
                Delay = TimeSpan.FromSeconds(1),
                BackoffType = DelayBackoffType.Constant,
                UseJitter = false,
            }));

        var steps = fromControlledClock
            ? new[] { TimeSpan.FromSeconds(1), HeaderDateOffset - TimeSpan.FromSeconds(1) }
            : new[] { HeaderDateOffset, TimeSpan.FromDays(300) };

        var label = fromControlledClock
            ? "controlled pipeline, HTTP-date 5 s ahead on the controlled clock"
            : "controlled pipeline, HTTP-date 5 s ahead on the system clock";

        var measurement = await ClockMeasurements.MeasureAsync(new ClockMeasurementRequest(
            chain,
            Window,
            fake.Advance,
            steps,
            fromControlledClock ? HeaderDateOffset + TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(1),
            cancellationToken)).ConfigureAwait(false);

        ClockMeasurements.Report(report, label, measurement);
        report.Fact($"{label}: date sent with the scripted response", date.ToString("O", CultureInfo.InvariantCulture));

        if (fromControlledClock)
        {
            // A date that matches the injected clock but not the wall clock is treated as already past.
            summary.DateDeltaComputedFromWallClock = measurement.CompletedImmediately;
        }
        else
        {
            // A date relative to the wall clock produces the intended delay, and that wait follows the injected clock.
            summary.DateWaitDrivenByInjectedClock = !measurement.CompletedImmediately && measurement.CompletedAfterAdvance;
        }
    }

    private static async Task PastDateOnControlledPipelineAsync(ProbeReport report, Summary summary, CancellationToken cancellationToken)
    {
        var fake = new FakeTimeProvider(ControlledStart);
        var date = fake.GetUtcNow() - TimeSpan.FromMinutes(1);

        using var chain = Chains.Controlled(
            "probe-retryafter-past-date",
            new[]
            {
                ScriptStep.Response(HttpStatusCode.ServiceUnavailable, retryAfterDate: date),
                ScriptStep.Success(),
            },
            fake,
            pipeline => pipeline.AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = 1,
                Delay = TimeSpan.FromSeconds(1),
                BackoffType = DelayBackoffType.Constant,
                UseJitter = false,
            }));

        var steps = new[] { TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1) };
        var measurement = await ClockMeasurements.MeasureAsync(new ClockMeasurementRequest(
            chain,
            Window,
            fake.Advance,
            steps,
            TimeSpan.FromSeconds(3),
            cancellationToken)).ConfigureAwait(false);

        ClockMeasurements.Report(report, "controlled pipeline, HTTP-date in the past", measurement);

        summary.PastDateProducesImmediateRetry = measurement.CompletedImmediately;
    }

    private static string Gap(ProbeChain chain)
    {
        var records = chain.Terminal.Records;
        return records.Count < 2 ? "no retry observed" : ProbeReport.Format(records[1].SinceStart - records[0].SinceStart);
    }

    private sealed class Summary
    {
        public bool DeltaControllableOnStandardHandler { get; set; }

        public bool DeltaDrivesDelayAboveShorterBackoff { get; set; }

        public bool DeltaOverridesLongerConfiguredBackoff { get; set; }

        public bool DateDeltaComputedFromWallClock { get; set; }

        public bool DateWaitDrivenByInjectedClock { get; set; }

        public bool PastDateProducesImmediateRetry { get; set; }

        public string Describe(bool deltasAreDeterministic, bool datesAreDeterministic)
        {
            var deltas =
                $"delta seconds (deterministic: {Yes(deltasAreDeterministic)}): " +
                $"a header above the configured backoff drives the delay on the injected clock: {Yes(DeltaDrivesDelayAboveShorterBackoff)}; " +
                $"a header replaces a longer configured backoff instead of waiting for it: {Yes(DeltaOverridesLongerConfiguredBackoff)}; " +
                $"the standard handler follows a registered TimeProvider: {Yes(DeltaControllableOnStandardHandler)}";

            var dates =
                $"HTTP-date (deterministic: {Yes(datesAreDeterministic)}): " +
                $"the date delta is computed against the wall clock: {Yes(DateDeltaComputedFromWallClock)}; " +
                $"the resulting wait follows the injected clock: {Yes(DateWaitDrivenByInjectedClock)}; " +
                $"a date in the past retries immediately: {Yes(PastDateProducesImmediateRetry)}";

            return $"{deltas}; {dates}";
        }

        private static string Yes(bool value) => value ? "yes" : "no";
    }
}
