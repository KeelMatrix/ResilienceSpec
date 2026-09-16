using System.Net;

namespace KeelMatrix.ResilienceSpec;

/// <summary>Describes how one attempt at the scripted downstream ended.</summary>
public enum HttpAttemptOutcome
{
    /// <summary>The attempt received the scripted response.</summary>
    Response,

    /// <summary>The attempt failed with the scripted network-like exception.</summary>
    NetworkError,

    /// <summary>The attempt never received an answer and ended through cancellation or a timeout.</summary>
    Abandoned,

    /// <summary>The attempt had no scripted step left to consume.</summary>
    ScriptExhausted,
}

/// <summary>
/// A privacy-safe record of one attempt that reached the scripted downstream.
/// </summary>
/// <remarks>
/// The record deliberately contains only the attempt ordinal, the HTTP method, the broad outcome, the scripted
/// status or <c>Retry-After</c> value, and injected-clock timing. Request URIs, query strings, headers, cookies,
/// authorization values, bodies, and exception messages are never captured.
/// </remarks>
/// <param name="Ordinal">The one-based attempt number, in the order attempts reached the downstream.</param>
/// <param name="Method">The HTTP method of the attempt.</param>
/// <param name="Outcome">The broad outcome of the attempt.</param>
/// <param name="StatusCode">The scripted status code, when the attempt received a response.</param>
/// <param name="RetryAfter">The scripted <c>Retry-After</c> value, when the response carried one.</param>
/// <param name="StartedAfter">
/// The injected-clock time between the start of the scenario and this attempt, or <see langword="null"/> when no
/// controllable clock was supplied.
/// </param>
/// <param name="Duration">
/// The injected-clock duration of the attempt, or <see langword="null"/> when no controllable clock was supplied.
/// </param>
public sealed record HttpAttempt(
    int Ordinal,
    HttpMethod Method,
    HttpAttemptOutcome Outcome,
    HttpStatusCode? StatusCode,
    TimeSpan? RetryAfter,
    TimeSpan? StartedAfter,
    TimeSpan? Duration);
