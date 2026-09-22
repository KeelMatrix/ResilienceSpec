using System.Net;

namespace KeelMatrix.ResilienceSpec;

/// <summary>Describes how a scenario run ended from the caller's point of view.</summary>
public enum ResilienceResultKind
{
    /// <summary>The client returned a response.</summary>
    Response,

    /// <summary>
    /// The request ended through a recognized timeout contract from the client or its resilience strategy, without
    /// caller cancellation. The attempt report shows the work observed before that outcome.
    /// </summary>
    Timeout,

    /// <summary>The caller's cancellation token was cancelled when the run ended, so cancellation is reported.</summary>
    Canceled,

    /// <summary>A scripted or downstream exception surfaced to the caller without a response, including unrelated cancellation-shaped failures.</summary>
    DownstreamError,

    /// <summary>The scripted downstream was called more times than the script provides steps.</summary>
    ScriptExhausted,

    /// <summary>The script was consumed by more than one in-flight request.</summary>
    ConcurrentUse,

    /// <summary>The request was still pending when the scenario stopped observing it.</summary>
    Pending,
}

/// <summary>The outcome of one scenario run, including the final caller-visible response or exception.</summary>
public sealed class ResilienceResult : IDisposable
{
    private ResilienceResult(
        ResilienceResultKind kind,
        HttpResponseMessage? response,
        Exception? exception,
        TimeSpan virtualElapsed,
        HttpAttemptReport report)
    {
        Kind = kind;
        Response = response;
        Exception = exception;
        VirtualElapsed = virtualElapsed;
        Report = report;
    }

    /// <summary>Gets how the run ended.</summary>
    public ResilienceResultKind Kind { get; }

    /// <summary>
    /// Gets the final response, which the caller owns and which this result disposes, or <see langword="null"/>
    /// when the run ended without a response.
    /// </summary>
    public HttpResponseMessage? Response { get; }

    /// <summary>Gets the exception the run surfaced to the caller, or <see langword="null"/> when a response was returned.</summary>
    public Exception? Exception { get; }

    /// <summary>Gets the status code of the final response, or <see langword="null"/> when no response was returned.</summary>
    public HttpStatusCode? StatusCode => Response?.StatusCode;

    /// <summary>Gets the injected-clock time the scenario advanced before the run finished.</summary>
    public TimeSpan VirtualElapsed { get; }

    /// <summary>Gets the attempt report of this run, frozen at the moment the run finished.</summary>
    public HttpAttemptReport Report { get; }

    /// <summary>Gets a value indicating whether the request was still pending when observation stopped.</summary>
    public bool IsPending => Kind == ResilienceResultKind.Pending;

    /// <summary>Disposes the final response, when there is one.</summary>
    public void Dispose() => Response?.Dispose();

    internal void RecordAssertion(bool passed) => Report.RecordAssertion(passed);

    internal static ResilienceResult ForResponse(HttpResponseMessage response, TimeSpan virtualElapsed, HttpAttemptReport report) =>
        new(ResilienceResultKind.Response, response, null, virtualElapsed, report);

    internal static ResilienceResult ForException(Exception exception, bool callerCanceled, TimeSpan virtualElapsed, HttpAttemptReport report) =>
        new(Classify(exception, callerCanceled), null, exception, virtualElapsed, report);

    internal static ResilienceResult Pending(TimeSpan virtualElapsed, HttpAttemptReport report) =>
        new(ResilienceResultKind.Pending, null, null, virtualElapsed, report);

    private static ResilienceResultKind Classify(Exception exception, bool callerCanceled)
    {
        if (Inner<ScriptExhaustedException>(exception) is not null)
        {
            return ResilienceResultKind.ScriptExhausted;
        }

        if (Inner<ConcurrentScriptUseException>(exception) is not null)
        {
            return ResilienceResultKind.ConcurrentUse;
        }

        if (callerCanceled)
        {
            return ResilienceResultKind.Canceled;
        }

        if (exception is TimeoutException || IsSupportedStrategyTimeout(exception) || IsNativeHttpClientTimeout(exception))
        {
            return ResilienceResultKind.Timeout;
        }

        return ResilienceResultKind.DownstreamError;
    }

    private static bool IsSupportedStrategyTimeout(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            // Microsoft.Extensions.Http.Resilience exposes Polly's public TimeoutRejectedException contract. Keep the
            // core package free of a Polly runtime dependency while recognizing that documented integration outcome.
            if (string.Equals(
                    current.GetType().FullName,
                    "Polly.Timeout.TimeoutRejectedException",
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsNativeHttpClientTimeout(Exception exception) =>
        exception is OperationCanceledException && Inner<TimeoutException>(exception) is not null;

    private static TException? Inner<TException>(Exception exception)
        where TException : Exception
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is TException match)
            {
                return match;
            }
        }

        return null;
    }
}
