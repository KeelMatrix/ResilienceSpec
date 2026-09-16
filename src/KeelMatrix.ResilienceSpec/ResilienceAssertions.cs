using System.Globalization;
using System.Net;
using System.Text;

namespace KeelMatrix.ResilienceSpec;

/// <summary>
/// Assertions over the observable behaviour of a client under test: attempt count, method sequence, final
/// response or exception, and the deterministic timing subset.
/// </summary>
/// <remarks>
/// Every failure reports the expectation, the observation, and the compact local timeline. Request URIs, headers,
/// bodies, and exception messages are never echoed. An assertion that needs injected-clock timing fails with
/// <see cref="MissingTimeProviderException"/> when the scenario has no controllable clock.
/// </remarks>
public static class ResilienceAssertions
{
    /// <summary>Asserts that exactly the expected number of attempts reached the scripted downstream.</summary>
    /// <param name="report">The attempt report to assert on.</param>
    /// <param name="expected">The exact expected attempt count.</param>
    /// <returns>The same report, so assertions can be chained.</returns>
    /// <exception cref="ResilienceAssertionException">The attempt count differs.</exception>
    public static HttpAttemptReport ShouldHaveAttempts(this HttpAttemptReport report, int expected)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (expected < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expected), expected, "An expected attempt count must not be negative.");
        }

        var actual = report.AttemptCount;
        var passed = actual == expected;
        report.RecordAssertion(passed);

        return passed
            ? report
            : throw Fail(report, $"exactly {expected} attempt(s) at the scripted downstream", $"the downstream served {actual} attempt(s)");
    }

    /// <summary>Asserts that no more than the given number of attempts reached the scripted downstream.</summary>
    /// <param name="report">The attempt report to assert on.</param>
    /// <param name="maximum">The largest acceptable attempt count.</param>
    /// <returns>The same report, so assertions can be chained.</returns>
    /// <exception cref="ResilienceAssertionException">More attempts were observed.</exception>
    public static HttpAttemptReport ShouldHaveAtMostAttempts(this HttpAttemptReport report, int maximum)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (maximum < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximum), maximum, "A maximum attempt count must not be negative.");
        }

        var actual = report.AttemptCount;
        var passed = actual <= maximum;
        report.RecordAssertion(passed);

        return passed
            ? report
            : throw Fail(report, $"at most {maximum} attempt(s) at the scripted downstream", $"the downstream served {actual} attempt(s)");
    }

    /// <summary>Asserts the exact sequence of HTTP methods that reached the scripted downstream.</summary>
    /// <param name="report">The attempt report to assert on.</param>
    /// <param name="methods">The expected methods, in attempt order.</param>
    /// <returns>The same report, so assertions can be chained.</returns>
    /// <exception cref="ResilienceAssertionException">The observed method sequence differs.</exception>
    public static HttpAttemptReport ShouldHaveMethodSequence(this HttpAttemptReport report, params HttpMethod[] methods)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(methods);

        if (methods.Length == 0)
        {
            throw new ArgumentException("An expected method sequence must contain at least one method.", nameof(methods));
        }

        foreach (var method in methods)
        {
            ArgumentNullException.ThrowIfNull(method);
        }

        var passed = report.AttemptCount == methods.Length;
        if (passed)
        {
            for (var index = 0; index < methods.Length; index++)
            {
                if (!string.Equals(report.Attempts[index].Method.Method, methods[index].Method, StringComparison.OrdinalIgnoreCase))
                {
                    passed = false;
                    break;
                }
            }
        }

        report.RecordAssertion(passed);

        return passed
            ? report
            : throw Fail(
                report,
                $"the method sequence {Describe(methods)}",
                $"the downstream observed {Describe(report)}");
    }

    /// <summary>Asserts that one HTTP method was never repeated, which is the invariant that protects unsafe requests.</summary>
    /// <param name="report">The attempt report to assert on.</param>
    /// <param name="method">The method that must not be retried.</param>
    /// <returns>The same report, so assertions can be chained.</returns>
    /// <exception cref="ResilienceAssertionException">The method reached the downstream more than once.</exception>
    public static HttpAttemptReport ShouldNotHaveRetried(this HttpAttemptReport report, HttpMethod method)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(method);

        var count = 0;
        foreach (var attempt in report.Attempts)
        {
            if (string.Equals(attempt.Method.Method, method.Method, StringComparison.OrdinalIgnoreCase))
            {
                count++;
            }
        }

        var passed = count <= 1;
        report.RecordAssertion(passed);

        return passed
            ? report
            : throw Fail(
                report,
                $"a {method.Method} request to reach the scripted downstream at most once",
                $"the downstream served {count} {method.Method} attempt(s)");
    }

    /// <summary>
    /// Asserts that every scripted <c>Retry-After</c> delay was honoured: the following attempt waited for the
    /// advertised injected-clock time.
    /// </summary>
    /// <param name="report">The attempt report to assert on.</param>
    /// <returns>The same report, so assertions can be chained.</returns>
    /// <exception cref="MissingTimeProviderException">The scenario has no controllable clock.</exception>
    /// <exception cref="ResilienceAssertionException">No response advertised <c>Retry-After</c>, or it was not honoured.</exception>
    public static HttpAttemptReport ShouldRespectRetryAfter(this HttpAttemptReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        RequireTiming(report, nameof(ShouldRespectRetryAfter));

        var step = report.ObservationStep!.Value;
        var advertised = false;
        for (var index = 0; index + 1 < report.AttemptCount; index++)
        {
            if (report.Attempts[index].RetryAfter is not { } delay)
            {
                continue;
            }

            advertised = true;
            var observed = Interval(report.Attempts[index], report.Attempts[index + 1]);
            if (observed < delay || observed > delay + step)
            {
                report.RecordAssertion(false);
                throw Fail(
                    report,
                    $"the attempt after a Retry-After of {TimeFormat.Describe(delay)} to wait for it",
                    $"the next attempt followed after {TimeFormat.Describe(observed)}");
            }
        }

        report.RecordAssertion(advertised);

        return advertised
            ? report
            : throw Fail(
                report,
                "at least one scripted response to advertise Retry-After",
                "no recorded attempt carried a Retry-After value");
    }

    /// <summary>Asserts the injected-clock delay between every pair of consecutive attempts.</summary>
    /// <param name="report">The attempt report to assert on.</param>
    /// <param name="expected">The expected inter-attempt delay.</param>
    /// <returns>The same report, so assertions can be chained.</returns>
    /// <exception cref="MissingTimeProviderException">The scenario has no controllable clock.</exception>
    /// <exception cref="ResilienceAssertionException">The observed delays differ from the expectation.</exception>
    public static HttpAttemptReport ShouldHaveRetryDelay(this HttpAttemptReport report, TimeSpan expected)
    {
        ArgumentNullException.ThrowIfNull(report);
        RequireTiming(report, nameof(ShouldHaveRetryDelay));

        if (report.AttemptCount < 2)
        {
            report.RecordAssertion(false);
            throw Fail(
                report,
                "at least two attempts so that a retry delay can be observed",
                $"the downstream served {report.AttemptCount} attempt(s)");
        }

        var step = report.ObservationStep!.Value;
        var intervals = new List<TimeSpan>(report.AttemptCount - 1);
        var passed = true;
        for (var index = 0; index + 1 < report.AttemptCount; index++)
        {
            var observed = Interval(report.Attempts[index], report.Attempts[index + 1]);
            intervals.Add(observed);
            passed &= observed >= expected && observed <= expected + step;
        }

        report.RecordAssertion(passed);

        return passed
            ? report
            : throw Fail(
                report,
                $"every retry to wait {TimeFormat.Describe(expected)} before the next attempt",
                $"the observed inter-attempt delays were {string.Join(", ", intervals.ConvertAll(TimeFormat.Describe))}");
    }

    /// <summary>Asserts how long one attempt lasted on the injected clock.</summary>
    /// <param name="report">The attempt report to assert on.</param>
    /// <param name="ordinal">The one-based attempt ordinal.</param>
    /// <param name="expected">The expected injected-clock duration of that attempt.</param>
    /// <returns>The same report, so assertions can be chained.</returns>
    /// <exception cref="MissingTimeProviderException">The scenario has no controllable clock.</exception>
    /// <exception cref="ResilienceAssertionException">The attempt is missing or lasted a different time.</exception>
    public static HttpAttemptReport ShouldHaveAttemptDuration(this HttpAttemptReport report, int ordinal, TimeSpan expected)
    {
        ArgumentNullException.ThrowIfNull(report);
        RequireTiming(report, nameof(ShouldHaveAttemptDuration));

        HttpAttempt? attempt = null;
        foreach (var candidate in report.Attempts)
        {
            if (candidate.Ordinal == ordinal)
            {
                attempt = candidate;
                break;
            }
        }

        if (attempt?.Duration is not { } observed)
        {
            report.RecordAssertion(false);
            throw Fail(
                report,
                $"attempt #{ordinal} to be recorded",
                "no attempt with that ordinal reached the scripted downstream");
        }

        var step = report.ObservationStep!.Value;
        var passed = observed >= expected && observed <= expected + step;
        report.RecordAssertion(passed);

        return passed
            ? report
            : throw Fail(
                report,
                $"attempt #{ordinal} to last {TimeFormat.Describe(expected)}",
                $"it lasted {TimeFormat.Describe(observed)}");
    }

    /// <summary>Asserts the total injected-clock time the run needed to settle.</summary>
    /// <param name="report">The attempt report to assert on.</param>
    /// <param name="expected">The expected injected-clock time until the run settled.</param>
    /// <returns>The same report, so assertions can be chained.</returns>
    /// <exception cref="MissingTimeProviderException">The scenario has no controllable clock.</exception>
    /// <exception cref="ResilienceAssertionException">The run has not settled or settled at a different time.</exception>
    public static HttpAttemptReport ShouldHaveSettledAtVirtualTime(this HttpAttemptReport report, TimeSpan expected)
    {
        ArgumentNullException.ThrowIfNull(report);
        RequireTiming(report, nameof(ShouldHaveSettledAtVirtualTime));

        if (!report.IsSettled || report.SettledVirtualElapsed is not { } observed)
        {
            report.RecordAssertion(false);
            throw Fail(
                report,
                "a finished scenario run before its total injected-clock time is asserted",
                "the run has not finished yet");
        }

        var step = report.ObservationStep!.Value;
        var passed = observed >= expected && observed <= expected + step;
        report.RecordAssertion(passed);

        return passed
            ? report
            : throw Fail(
                report,
                $"the run to settle after {TimeFormat.Describe(expected)} of injected-clock time",
                $"it settled after {TimeFormat.Describe(observed)}");
    }

    /// <summary>Asserts the status code of the final response.</summary>
    /// <param name="result">The scenario result to assert on.</param>
    /// <param name="expected">The expected status code.</param>
    /// <returns>The same result, so assertions can be chained.</returns>
    /// <exception cref="ResilienceAssertionException">The final status differs.</exception>
    public static ResilienceResult ShouldHaveStatus(this ResilienceResult result, HttpStatusCode expected)
    {
        ArgumentNullException.ThrowIfNull(result);

        var actual = result.StatusCode;
        var passed = actual == expected;
        result.RecordAssertion(passed);

        return passed
            ? result
            : throw Fail(
                result,
                $"the final response to have status {(int)expected} {expected}",
                actual is { } value ? $"the run returned status {(int)value} {value}" : $"the run ended as {Describe(result)}");
    }

    /// <summary>Asserts how the run ended from the caller's point of view.</summary>
    /// <param name="result">The scenario result to assert on.</param>
    /// <param name="expected">The expected result kind.</param>
    /// <returns>The same result, so assertions can be chained.</returns>
    /// <exception cref="ResilienceAssertionException">The run ended differently.</exception>
    public static ResilienceResult ShouldHaveKind(this ResilienceResult result, ResilienceResultKind expected)
    {
        ArgumentNullException.ThrowIfNull(result);

        var passed = result.Kind == expected;
        result.RecordAssertion(passed);

        return passed
            ? result
            : throw Fail(result, $"the run to end as {expected}", $"the run ended as {Describe(result)}");
    }

    /// <summary>Asserts that the request was still pending when the scenario stopped observing it.</summary>
    /// <param name="result">The scenario result to assert on.</param>
    /// <returns>The same result, so assertions can be chained.</returns>
    /// <exception cref="ResilienceAssertionException">The run had already finished.</exception>
    public static ResilienceResult ShouldBePending(this ResilienceResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var passed = result.IsPending;
        result.RecordAssertion(passed);

        return passed
            ? result
            : throw Fail(result, "the request to still be pending", $"the run ended as {Describe(result)}");
    }

    /// <summary>Asserts that the run surfaced the given exception type, directly or as an inner exception.</summary>
    /// <typeparam name="TException">The expected exception type.</typeparam>
    /// <param name="result">The scenario result to assert on.</param>
    /// <returns>The same result, so assertions can be chained.</returns>
    /// <exception cref="ResilienceAssertionException">No matching exception reached the caller.</exception>
    public static ResilienceResult ShouldHaveException<TException>(this ResilienceResult result)
        where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(result);

        var passed = false;
        for (var current = result.Exception; current is not null; current = current.InnerException)
        {
            if (current is TException)
            {
                passed = true;
                break;
            }
        }

        result.RecordAssertion(passed);

        return passed
            ? result
            : throw Fail(
                result,
                $"the run to surface {typeof(TException).FullName}",
                result.Exception is { } exception ? $"the run surfaced {exception.GetType().FullName}" : "the run surfaced no exception");
    }

    private static void RequireTiming(HttpAttemptReport report, string assertion)
    {
        var timed = report.HasTiming && report.AttemptCount > 0;
        if (timed)
        {
            foreach (var attempt in report.Attempts)
            {
                if (attempt.StartedAfter is null || attempt.Duration is null)
                {
                    timed = false;
                    break;
                }
            }
        }

        if (timed)
        {
            return;
        }

        report.RecordAssertion(false);
        throw new MissingTimeProviderException(
            $"{assertion} requires an injected clock and at least one timed attempt. Create the scenario with a controllable clock and " +
            "its advance operation, for example new ResilienceScenario(script, clock, clock.Advance), and let the same clock drive the " +
            "resilience pipeline. The package never falls back to wall-clock sleeps or elapsed-time tolerances.");
    }

    private static TimeSpan Interval(HttpAttempt attempt, HttpAttempt next) =>
        next.StartedAfter!.Value - (attempt.StartedAfter!.Value + attempt.Duration!.Value);

    private static string Describe(HttpMethod[] methods) =>
        string.Join(" -> ", Array.ConvertAll(methods, method => method.Method));

    private static string Describe(HttpAttemptReport report) =>
        report.AttemptCount == 0
            ? "no attempts"
            : string.Join(" -> ", report.Attempts.Select(attempt => attempt.Method.Method));

    private static string Describe(ResilienceResult result) =>
        result.Kind switch
        {
            ResilienceResultKind.Response => $"status {(int)result.StatusCode!.Value} {result.StatusCode}",
            ResilienceResultKind.Pending => "pending",
            _ when result.Exception is { } exception => $"{result.Kind} ({exception.GetType().FullName})",
            _ => result.Kind.ToString(),
        };

    private static ResilienceAssertionException Fail(HttpAttemptReport report, string expectation, string observation) =>
        new(BuildMessage(expectation, observation, report.DescribeTimeline()));

    private static ResilienceAssertionException Fail(ResilienceResult result, string expectation, string observation) =>
        new(BuildMessage(expectation, observation, result.Report.DescribeTimeline()));

    private static string BuildMessage(string expectation, string observation, string timeline)
    {
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"Expected {expectation}, but {observation}.");
        builder.AppendLine();
        builder.AppendLine();
        if (timeline.Length == 0)
        {
            builder.Append("Timeline: no attempt reached the scripted downstream.");
        }
        else
        {
            builder.AppendLine("Timeline:");
            builder.Append(timeline);
        }

        return builder.ToString();
    }
}
