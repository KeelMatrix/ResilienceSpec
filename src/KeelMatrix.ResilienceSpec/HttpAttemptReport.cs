using System.Collections.ObjectModel;
using System.Net;

namespace KeelMatrix.ResilienceSpec;

/// <summary>
/// An immutable snapshot of the attempts that reached one scripted downstream, in arrival order.
/// </summary>
/// <remarks>
/// The report is the local evidence of observable behaviour. It never contains request or response payloads, and
/// every line of its timeline is safe to print in test output. The recorded timeline is bounded by
/// <see cref="MaximumRecordedAttempts"/> so that a client under test cannot grow attempt state inside the test
/// process without bound.
/// </remarks>
public sealed class HttpAttemptReport
{
    /// <summary>
    /// The maximum number of attempts one report records. A script cannot describe more attempts than
    /// <see cref="HttpFaultScript.MaximumSteps"/> steps, so the recorded timeline keeps at most that many attempts.
    /// </summary>
    public const int MaximumRecordedAttempts = HttpFaultScript.MaximumSteps;

    private readonly ReadOnlyCollection<HttpAttempt> _attempts;

    internal HttpAttemptReport(
        HttpAttempt[] attempts,
        int attemptCount,
        bool overflowed,
        bool settled,
        bool observationCutoff,
        TimeSpan? settledVirtualElapsed,
        TimeSpan? observationStep,
        ScenarioTelemetry telemetry)
    {
        _attempts = Array.AsReadOnly(attempts);
        AttemptCount = attemptCount;
        IsOverflowed = overflowed;
        IsSettled = settled;
        IsObservationCutoff = observationCutoff;
        SettledVirtualElapsed = settledVirtualElapsed;
        ObservationStep = observationStep;
        Telemetry = telemetry;
        Timeline = BuildTimeline(_attempts, attemptCount, overflowed);
    }

    /// <summary>
    /// Gets the attempts that reached the scripted downstream, ordered by attempt ordinal. The list holds at most
    /// <see cref="MaximumRecordedAttempts"/> attempts; <see cref="IsOverflowed"/> reports when more attempts were
    /// served than could be recorded.
    /// </summary>
    public IReadOnlyList<HttpAttempt> Attempts => _attempts;

    /// <summary>
    /// Gets the exact number of attempts that reached the scripted downstream. This is the served total, so it stays
    /// truthful even when <see cref="IsOverflowed"/> is <see langword="true"/> and the timeline had to stop
    /// recording.
    /// </summary>
    public int AttemptCount { get; }

    /// <summary>
    /// Gets a value indicating whether the downstream served more attempts than <see cref="MaximumRecordedAttempts"/>.
    /// The timeline then records the first <see cref="MaximumRecordedAttempts"/> attempts, ends with an explicit
    /// truncation line, and every assertion that judges attempt state fails with
    /// <see cref="AttemptStateOverflowException"/> instead of evaluating an incomplete timeline.
    /// </summary>
    public bool IsOverflowed { get; }

    /// <summary>Gets the last recorded attempt, or <see langword="null"/> when no attempt was recorded.</summary>
    public HttpAttempt? LastAttempt => _attempts.Count == 0 ? null : _attempts[_attempts.Count - 1];

    /// <summary>Gets a value indicating whether the scenario run that produced this report has finished.</summary>
    public bool IsSettled { get; }

    /// <summary>
    /// Gets a value indicating whether observation stopped before the logical request genuinely settled. A cutoff is
    /// not evidence that a resilience timeout or completed request occurred.
    /// </summary>
    public bool IsObservationCutoff { get; }

    /// <summary>
    /// Gets the injected-clock time the scenario advanced before the run finished, or <see langword="null"/> when
    /// the run has not finished or no controllable clock was supplied.
    /// </summary>
    public TimeSpan? SettledVirtualElapsed { get; }

    /// <summary>
    /// Gets the injected-clock granularity of the scenario, or <see langword="null"/> when no controllable clock
    /// was supplied. Timing assertions compare an observed duration with the expected value, allowing at most one
    /// advance step of additional time.
    /// </summary>
    public TimeSpan? ObservationStep { get; }

    /// <summary>Gets a value indicating whether timing assertions are available for this report.</summary>
    public bool HasTiming => ObservationStep is not null;

    /// <summary>
    /// Gets the compact local timeline: one line per recorded attempt, followed by one explicit truncation line when
    /// <see cref="IsOverflowed"/> is <see langword="true"/>.
    /// </summary>
    public IReadOnlyList<string> Timeline { get; }

    internal ScenarioTelemetry Telemetry { get; }

    /// <summary>Renders the compact local timeline as text without echoing request data.</summary>
    /// <returns>One line per attempt, or an empty string when no attempt was recorded.</returns>
    public string DescribeTimeline() => string.Join(Environment.NewLine, Timeline);

    internal void RecordAssertion(bool passed) => Telemetry.RecordAssertionEvaluation(passed, AttemptCount);

    /// <summary>
    /// Rejects an assertion that would otherwise judge an incomplete timeline, so an overflowed run can never look
    /// like a passing or accurately counted one.
    /// </summary>
    /// <param name="assertion">The assertion that was about to be evaluated.</param>
    /// <exception cref="AttemptStateOverflowException">The timeline is incomplete because the bound was exceeded.</exception>
    internal void RequireCompleteTimeline(string assertion)
    {
        if (!IsOverflowed)
        {
            return;
        }

        RecordAssertion(false);
        throw new AttemptStateOverflowException(
            $"{assertion} cannot be evaluated: the scripted downstream served {AttemptCount} attempt(s), but the recorded " +
            $"timeline keeps at most {MaximumRecordedAttempts} attempts (HttpAttemptReport.MaximumRecordedAttempts), so the " +
            "attempt count, method sequence, and timing of this run cannot be judged from it. This usually means the client " +
            "under test retried a harness failure such as ScriptExhaustedException more often than a script can describe. " +
            "Reduce the client's configured maximum attempts, or assert the scripted behaviour with a scenario whose expected " +
            "attempt count the timeline can hold.");
    }

    private static IReadOnlyList<string> BuildTimeline(
        ReadOnlyCollection<HttpAttempt> attempts,
        int attemptCount,
        bool overflowed)
    {
        if (attempts.Count == 0)
        {
            return Array.Empty<string>();
        }

        var lines = new List<string>(attempts.Count + 1);
        foreach (var attempt in attempts)
        {
            var line = $"#{attempt.Ordinal} {attempt.Method.Method} -> {Describe(attempt)}";
            if (attempt.StartedAfter is { } started && attempt.Duration is { } duration)
            {
                line += $" | started +{TimeFormat.Describe(started)} | lasted {TimeFormat.Describe(duration)}";
            }

            lines.Add(line);
        }

        if (overflowed)
        {
            lines.Add(
                $"timeline capped: {attemptCount - attempts.Count} further attempt(s) reached the scripted downstream after " +
                $"#{attempts[attempts.Count - 1].Ordinal} and were served but not recorded. A report keeps at most " +
                $"{MaximumRecordedAttempts} attempts (HttpAttemptReport.MaximumRecordedAttempts), IsOverflowed is true, and " +
                "assertions over this report fail with AttemptStateOverflowException.");
        }

        return lines.AsReadOnly();
    }

    private static string Describe(HttpAttempt attempt) => attempt.Outcome switch
    {
        HttpAttemptOutcome.Response when attempt.RetryAfter is { } delta =>
            $"response {(int)attempt.StatusCode!.Value} (retry-after {TimeFormat.Describe(delta)})",
        HttpAttemptOutcome.Response => $"response {(int)attempt.StatusCode!.Value}",
        HttpAttemptOutcome.NetworkError => "network error",
        HttpAttemptOutcome.ScriptExhausted => "script exhausted",
        _ => "no response",
    };
}
