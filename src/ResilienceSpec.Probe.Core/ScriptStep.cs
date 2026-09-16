using System.Net;

namespace ResilienceSpec.Probe;

/// <summary>The scripted downstream outcome for a single request attempt.</summary>
public enum ScriptStepKind
{
    /// <summary>The attempt receives a response with the scripted status code.</summary>
    Response,

    /// <summary>The attempt fails with an in-memory <see cref="HttpRequestException"/>.</summary>
    NetworkError,

    /// <summary>The attempt never receives an answer; it completes only when the attempt is canceled.</summary>
    Hang,
}

/// <summary>One step of a deterministic downstream script.</summary>
public sealed class ScriptStep
{
    private ScriptStep(ScriptStepKind kind, HttpStatusCode statusCode, TimeSpan? retryAfterDelta, DateTimeOffset? retryAfterDate)
    {
        Kind = kind;
        StatusCode = statusCode;
        RetryAfterDelta = retryAfterDelta;
        RetryAfterDate = retryAfterDate;
    }

    public ScriptStepKind Kind { get; }

    public HttpStatusCode StatusCode { get; }

    public TimeSpan? RetryAfterDelta { get; }

    public DateTimeOffset? RetryAfterDate { get; }

    /// <summary>Creates a step that answers with the given status code, optionally carrying <c>Retry-After</c>.</summary>
    public static ScriptStep Response(HttpStatusCode statusCode, TimeSpan? retryAfterDelta = null, DateTimeOffset? retryAfterDate = null)
    {
        if (retryAfterDelta.HasValue && retryAfterDate.HasValue)
        {
            throw new ArgumentException("A scripted response carries either a delta or an HTTP-date Retry-After value, never both.");
        }

        if (retryAfterDelta is { } delta && delta < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retryAfterDelta), "A Retry-After delta must not be negative.");
        }

        return new ScriptStep(ScriptStepKind.Response, statusCode, retryAfterDelta, retryAfterDate);
    }

    /// <summary>Creates a step that answers with <c>200 OK</c>.</summary>
    public static ScriptStep Success() => Response(HttpStatusCode.OK);

    /// <summary>Creates a step that fails the attempt with an in-memory network exception.</summary>
    public static ScriptStep NetworkError() => new(ScriptStepKind.NetworkError, default, null, null);

    /// <summary>Creates a step that models a downstream that never answers.</summary>
    public static ScriptStep Hang() => new(ScriptStepKind.Hang, default, null, null);

    /// <summary>Describes the step without exposing request or response payloads.</summary>
    public string Describe() => Kind switch
    {
        ScriptStepKind.Response when RetryAfterDelta.HasValue =>
            $"response {(int)StatusCode} (retry-after delta {RetryAfterDelta.Value.TotalSeconds:0.###}s)",
        ScriptStepKind.Response when RetryAfterDate.HasValue =>
            $"response {(int)StatusCode} (retry-after date {RetryAfterDate.Value:O})",
        ScriptStepKind.Response => $"response {(int)StatusCode}",
        ScriptStepKind.NetworkError => "network-error",
        _ => "hang",
    };
}
