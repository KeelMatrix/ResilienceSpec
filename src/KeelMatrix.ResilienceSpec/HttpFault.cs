using System.Net;

namespace KeelMatrix.ResilienceSpec;

/// <summary>Identifies what a scripted downstream does for one attempt.</summary>
internal enum HttpFaultKind
{
    /// <summary>The attempt receives a response with the scripted status code.</summary>
    Response,

    /// <summary>The attempt fails with a network-like <see cref="HttpRequestException"/>.</summary>
    NetworkError,

    /// <summary>The attempt never receives an answer and ends only through cancellation or a timeout.</summary>
    Timeout,

    /// <summary>The attempt waits for a duration on the injected clock before the wrapped fault is applied.</summary>
    Delay,
}

/// <summary>
/// One step of a deterministic downstream script: the outcome the scripted terminal handler produces for a
/// single HTTP attempt.
/// </summary>
/// <remarks>
/// A fault describes an outcome only. It never carries request or response payloads, headers other than the
/// controlled <c>Retry-After</c> value, or application data.
/// </remarks>
public sealed class HttpFault
{
    private HttpFault(HttpFaultKind kind, HttpStatusCode statusCode, TimeSpan? retryAfter, TimeSpan? delay, HttpFault? innerFault)
    {
        Kind = kind;
        StatusCode = statusCode;
        RetryAfter = retryAfter;
        DelayDuration = delay;
        InnerFault = innerFault;
    }

    /// <summary>Gets the delay this response advertises through the <c>Retry-After</c> header, when one was configured.</summary>
    public TimeSpan? RetryAfter { get; }

    internal HttpFaultKind Kind { get; }

    internal HttpStatusCode StatusCode { get; }

    internal TimeSpan? DelayDuration { get; }

    internal HttpFault? InnerFault { get; }

    /// <summary>Gets a value indicating whether this step models a failure rather than a successful response.</summary>
    internal bool IsFailure => Kind switch
    {
        HttpFaultKind.NetworkError or HttpFaultKind.Timeout => true,
        HttpFaultKind.Response => (int)StatusCode >= 400,
        _ => InnerFault!.IsFailure,
    };

    internal bool ContainsResponseFault => Kind switch
    {
        HttpFaultKind.Response => (int)StatusCode >= 400,
        HttpFaultKind.Delay => InnerFault!.ContainsResponseFault,
        _ => false,
    };

    internal bool ContainsExceptionFault => Kind switch
    {
        HttpFaultKind.NetworkError => true,
        HttpFaultKind.Delay => InnerFault!.ContainsExceptionFault,
        _ => false,
    };

    /// <summary>Creates a step that answers the attempt with the given status code.</summary>
    /// <param name="statusCode">The status code the scripted downstream returns; it must be between 0 and 999.</param>
    /// <param name="retryAfter">
    /// An optional <c>Retry-After</c> delay. The delta-seconds form is deterministic on an injected clock; the
    /// HTTP-date form is deliberately not supported. A value uses whole-millisecond precision and cannot exceed
    /// <see cref="int.MaxValue"/> milliseconds.
    /// </param>
    /// <returns>The script step.</returns>
    public static HttpFault Response(HttpStatusCode statusCode, TimeSpan? retryAfter = null)
    {
        if ((int)statusCode is < 0 or > 999)
        {
            throw new ArgumentOutOfRangeException(
                nameof(statusCode),
                statusCode,
                "A scripted response status must be representable by HttpResponseMessage (0 through 999).");
        }

        if (retryAfter is { } delta)
        {
            DurationContract.ValidateScriptDuration(
                nameof(retryAfter),
                delta,
                allowZero: true,
                "A Retry-After delta");
        }

        return new HttpFault(HttpFaultKind.Response, statusCode, retryAfter, null, null);
    }

    /// <summary>Creates a step that answers the attempt with <see cref="HttpStatusCode.OK"/>.</summary>
    /// <returns>The script step.</returns>
    public static HttpFault Success() => Response(HttpStatusCode.OK);

    /// <summary>Creates a step that fails the attempt with a network-like exception.</summary>
    /// <returns>The script step.</returns>
    public static HttpFault NetworkError() => new(HttpFaultKind.NetworkError, default, null, null, null);

    /// <summary>
    /// Creates a step whose downstream never answers, so the attempt can only end through a timeout strategy or
    /// through caller cancellation. No wall-clock delay is involved.
    /// </summary>
    /// <returns>The script step.</returns>
    public static HttpFault Timeout() => new(HttpFaultKind.Timeout, default, null, null, null);

    /// <summary>
    /// Creates a step that waits for the given duration on the injected clock and then applies the wrapped step.
    /// </summary>
    /// <param name="duration">The positive whole-millisecond amount of injected-clock time the downstream takes before it answers; it cannot exceed <see cref="int.MaxValue"/> milliseconds.</param>
    /// <param name="fault">The step applied when the wait completes.</param>
    /// <returns>The script step.</returns>
    public static HttpFault Delay(TimeSpan duration, HttpFault fault)
    {
        ArgumentNullException.ThrowIfNull(fault);

        DurationContract.ValidateScriptDuration(nameof(duration), duration, allowZero: false, "A scripted delay");

        if (fault.Kind == HttpFaultKind.Delay)
        {
            throw new ArgumentException("A scripted delay cannot wrap another delay step.", nameof(fault));
        }

        return new HttpFault(HttpFaultKind.Delay, default, null, duration, fault);
    }

    /// <summary>Describes the script step without exposing request or response data.</summary>
    /// <returns>A short description such as <c>response 503 (retry-after 2 s)</c>.</returns>
    public override string ToString() => Describe();

    internal string Describe() => Kind switch
    {
        HttpFaultKind.Response when RetryAfter is { } delta =>
            $"response {(int)StatusCode} (retry-after {TimeFormat.Describe(delta)})",
        HttpFaultKind.Response => $"response {(int)StatusCode}",
        HttpFaultKind.NetworkError => "network error",
        HttpFaultKind.Timeout => "no response",
        _ => $"delay {TimeFormat.Describe(DelayDuration!.Value)} then {InnerFault!.Describe()}",
    };
}

internal static class DurationContract
{
    internal static TimeSpan MaximumTaskDelay { get; } = TimeSpan.FromMilliseconds(int.MaxValue);

    internal static void ValidateScriptDuration(string parameterName, TimeSpan value, bool allowZero, string description)
    {
        if (value < TimeSpan.Zero || (!allowZero && value == TimeSpan.Zero))
        {
            var comparison = allowZero ? "must not be negative" : "must be greater than zero";
            throw new ArgumentOutOfRangeException(parameterName, value, $"{description} {comparison}.");
        }

        ValidateTaskDelayPrecisionAndRange(parameterName, value, description);
    }

    internal static void ValidateWatchdogDuration(string parameterName, TimeSpan value, string description)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, $"{description} must be greater than zero.");
        }

        ValidateTaskDelayPrecisionAndRange(parameterName, value, description);
    }

    private static void ValidateTaskDelayPrecisionAndRange(string parameterName, TimeSpan value, string description)
    {
        if (value > MaximumTaskDelay)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"{description} exceeds the maximum duration supported by the controlled Task.Delay timer ({MaximumTaskDelay}).");
        }

        if (value.Ticks % TimeSpan.TicksPerMillisecond != 0)
        {
            throw new ArgumentException(
                $"{description} must use whole-millisecond precision because the controlled Task.Delay timer executes " +
                "only the duration represented by its millisecond contract.",
                parameterName);
        }
    }
}
