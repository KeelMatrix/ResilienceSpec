using System.Diagnostics;

namespace ResilienceSpec.Probe.Runner;

/// <summary>One controlled-clock advance applied while a request was pending.</summary>
internal sealed record AdvanceOutcome(TimeSpan Step, int Attempts, bool Completed);

/// <summary>What one request did while a controlled clock was advanced in steps.</summary>
internal sealed record ClockMeasurement(
    bool CompletedImmediately,
    int AttemptsImmediately,
    bool CompletedAfterAdvance,
    int AdvancesApplied,
    int AttemptsAfterAdvance,
    TimeSpan AdvancedTotal,
    IReadOnlyList<AdvanceOutcome> Advances,
    bool CompletedOnSystemClock,
    TimeSpan WallElapsed,
    string Outcome)
{
    public int FinalAttempts => CompletedAfterAdvance ? AttemptsAfterAdvance : AttemptsImmediately;

    public bool Settled => CompletedImmediately || CompletedAfterAdvance || CompletedOnSystemClock;
}

internal static class ClockMeasurements
{
    /// <summary>
    /// Runs one request, observes it for a bounded window, then applies the given clock advances one at a time
    /// until it settles. The windows only distinguish "completed" from "still pending"; no assertion depends on
    /// an elapsed-time tolerance. When the request does not settle on the controlled clock, the probe waits a
    /// bounded time for the system clock so the two cases can be told apart, then cancels.
    /// </summary>
    public static async Task<ClockMeasurement> MeasureAsync(ClockMeasurementRequest request)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(request.CancellationToken);
        var clock = Stopwatch.StartNew();
        var pending = request.Chain.SendAsync(HttpMethod.Get, linked.Token);

        var completedImmediately = await Requests.CompletedWithinAsync(pending, request.Window).ConfigureAwait(false);
        var attemptsImmediately = request.Chain.Terminal.AttemptCount;

        var completedAfterAdvance = false;
        var advancesApplied = 0;
        var advancedTotal = TimeSpan.Zero;
        var advances = new List<AdvanceOutcome>();

        if (!completedImmediately)
        {
            foreach (var step in request.AdvanceSteps)
            {
                request.Advance(step);
                advancesApplied++;
                advancedTotal += step;

                var completed = await Requests.CompletedWithinAsync(pending, request.Window).ConfigureAwait(false);
                advances.Add(new AdvanceOutcome(step, request.Chain.Terminal.AttemptCount, completed));
                if (completed)
                {
                    completedAfterAdvance = true;
                    break;
                }
            }
        }

        var completedOnSystemClock = false;
        if (!completedImmediately && !completedAfterAdvance && request.SystemClockWindow is { } systemWindow)
        {
            completedOnSystemClock = await Requests.CompletedWithinAsync(pending, systemWindow).ConfigureAwait(false);
        }

        var settled = completedImmediately || completedAfterAdvance || completedOnSystemClock;
        var outcome = settled
            ? await Requests.DescribeAsync(pending).ConfigureAwait(false)
            : "request still pending";
        var attemptsAfterAdvance = request.Chain.Terminal.AttemptCount;

        if (!settled)
        {
            await Requests.CompleteAfterCancelAsync(linked, pending).ConfigureAwait(false);
        }

        clock.Stop();
        return new ClockMeasurement(
            completedImmediately,
            attemptsImmediately,
            completedAfterAdvance,
            advancesApplied,
            attemptsAfterAdvance,
            advancedTotal,
            advances,
            completedOnSystemClock,
            clock.Elapsed,
            outcome);
    }

    public static void Report(ProbeReport report, string label, ClockMeasurement measurement)
    {
        report.Fact($"{label}: attempts before any clock advance", measurement.AttemptsImmediately);
        report.Fact($"{label}: completed before any clock advance", measurement.CompletedImmediately);
        report.Items(
            $"{label}: controlled-clock trace",
            measurement.Advances.Select(advance =>
                $"advanced {ProbeReport.Format(advance.Step)}: attempts {advance.Attempts}, completed {advance.Completed}"));
        report.Fact($"{label}: attempts that reached the terminal handler", measurement.FinalAttempts);
        report.Fact($"{label}: total controlled-clock advance applied", ProbeReport.Format(measurement.AdvancedTotal));
        report.Fact($"{label}: completed on the system clock instead", measurement.CompletedOnSystemClock);
        report.Fact(
            $"{label}: wall-clock time spent by the probe, including its bounded observation windows (reference only)",
            ProbeReport.Format(measurement.WallElapsed));
        report.Fact($"{label}: outcome", measurement.Outcome);
    }
}

internal sealed record ClockMeasurementRequest(
    ProbeChain Chain,
    TimeSpan Window,
    Action<TimeSpan> Advance,
    IReadOnlyList<TimeSpan> AdvanceSteps,
    TimeSpan? SystemClockWindow,
    CancellationToken CancellationToken);
