using System.Collections.ObjectModel;
using System.Net;

namespace KeelMatrix.ResilienceSpec;

/// <summary>
/// An immutable snapshot of the attempts that reached one scripted downstream, in arrival order.
/// </summary>
/// <remarks>
/// The report is the local evidence of observable behaviour. It never contains request or response payloads, and
/// every line of its timeline is safe to print in test output.
/// </remarks>
public sealed class HttpAttemptReport
{
    private readonly ReadOnlyCollection<HttpAttempt> _attempts;

    internal HttpAttemptReport(
        HttpAttempt[] attempts,
        bool settled,
        TimeSpan? settledVirtualElapsed,
        TimeSpan? observationStep,
        ScenarioTelemetry telemetry)
    {
        _attempts = Array.AsReadOnly(attempts);
        IsSettled = settled;
        SettledVirtualElapsed = settledVirtualElapsed;
        ObservationStep = observationStep;
        Telemetry = telemetry;
        Timeline = BuildTimeline(_attempts);
    }

    /// <summary>Gets the attempts that reached the scripted downstream, ordered by attempt ordinal.</summary>
    public IReadOnlyList<HttpAttempt> Attempts => _attempts;

    /// <summary>Gets the number of attempts that reached the scripted downstream.</summary>
    public int AttemptCount => _attempts.Count;

    /// <summary>Gets the last recorded attempt, or <see langword="null"/> when no attempt was recorded.</summary>
    public HttpAttempt? LastAttempt => _attempts.Count == 0 ? null : _attempts[_attempts.Count - 1];

    /// <summary>Gets a value indicating whether the scenario run that produced this report has finished.</summary>
    public bool IsSettled { get; }

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

    /// <summary>Gets the compact local timeline, one line per attempt.</summary>
    public IReadOnlyList<string> Timeline { get; }

    internal ScenarioTelemetry Telemetry { get; }

    /// <summary>Renders the compact local timeline as text without echoing request data.</summary>
    /// <returns>One line per attempt, or an empty string when no attempt was recorded.</returns>
    public string DescribeTimeline() => string.Join(Environment.NewLine, Timeline);

    internal void RecordAssertion(bool passed) => Telemetry.RecordAssertionEvaluation(passed, AttemptCount);

    private static IReadOnlyList<string> BuildTimeline(ReadOnlyCollection<HttpAttempt> attempts)
    {
        if (attempts.Count == 0)
        {
            return Array.Empty<string>();
        }

        var lines = new List<string>(attempts.Count);
        foreach (var attempt in attempts)
        {
            var line = $"#{attempt.Ordinal} {attempt.Method.Method} -> {Describe(attempt)}";
            if (attempt.StartedAfter is { } started && attempt.Duration is { } duration)
            {
                line += $" | started +{TimeFormat.Describe(started)} | lasted {TimeFormat.Describe(duration)}";
            }

            lines.Add(line);
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
