using System.Net.Http.Headers;

namespace KeelMatrix.ResilienceSpec;

/// <summary>
/// A terminal <see cref="HttpMessageHandler"/> that answers every attempt from a deterministic script instead of
/// the network.
/// </summary>
/// <remarks>
/// <para>
/// Install the handler as the innermost handler of the chain under test, for example with
/// <c>ConfigurePrimaryHttpMessageHandler</c>, so that the application's real delegating handlers and resilience
/// strategies stay in place and only the network boundary is replaced.
/// </para>
/// <para>
/// The handler answers in memory. It never resolves a name, opens a socket, or binds a listener, and its responses
/// carry no content and no headers other than the scripted <c>Retry-After</c> value.
/// </para>
/// </remarks>
public sealed class ScriptedHttpMessageHandler : HttpMessageHandler
{
    private const string NetworkErrorMessage = "The scripted downstream reported a network failure.";

    private readonly TimeProvider? _timeProvider;
    private readonly ScenarioObserver _observer;

    /// <summary>Initializes a new instance of the <see cref="ScriptedHttpMessageHandler"/> class.</summary>
    /// <param name="script">The script of downstream outcomes, one step per attempt.</param>
    /// <param name="timeProvider">
    /// The controllable clock the scripted delay steps run on, or <see langword="null"/> when the script uses no
    /// delay steps and no timing is asserted.
    /// </param>
    /// <param name="options">Scenario options, or <see langword="null"/> for <see cref="ResilienceScenarioOptions.Default"/>.</param>
    public ScriptedHttpMessageHandler(
        HttpFaultScript script,
        TimeProvider? timeProvider = null,
        ResilienceScenarioOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(script);
        options ??= ResilienceScenarioOptions.Default;
        options.Validate();

        if (script.RequiresControlledClock && timeProvider is null)
        {
            throw new MissingTimeProviderException(
                "The script contains a delay step, which is only deterministic on an injected clock. " +
                "Wrap the controllable provider in ResilienceScenarioClock and create the scenario with " +
                "clock.TimeProvider and clock.Advance.");
        }

        if (timeProvider is not null && !ResilienceScenarioClock.IsTrackingProvider(timeProvider))
        {
            throw new MissingTimeProviderException(
                "Direct ScriptedHttpMessageHandler use with timing requires a ResilienceScenarioClock. Wrap the " +
                "controllable provider and pass clock.TimeProvider so timing assertions cannot use wall-clock durations.");
        }

        _timeProvider = timeProvider;
        _observer = new ScenarioObserver(script, timeProvider, options);
    }

    /// <summary>Gets a snapshot of the attempts that reached this handler.</summary>
    public HttpAttemptReport Report => _observer.Snapshot();

    internal ScenarioObserver Observer => _observer;

    /// <summary>Sends one attempt through the scripted downstream.</summary>
    /// <param name="request">The request the chain under test produced.</param>
    /// <param name="cancellationToken">The token that ends a hanging attempt.</param>
    /// <returns>The scripted response.</returns>
    /// <exception cref="ScriptExhaustedException">The script has no step left for this attempt.</exception>
    /// <exception cref="ConcurrentScriptUseException">The script is already serving an in-flight attempt.</exception>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var attempt = _observer.BeginAttempt(request.Method);
        var fault = attempt.Entry.Fault;
        if (fault is null)
        {
            attempt.Complete(HttpAttemptOutcome.ScriptExhausted);
            throw new ScriptExhaustedException(
                $"The scripted downstream was called {attempt.Entry.Ordinal} time(s), but the script provides only " +
                $"{_observer.StepCount} step(s). Extend the script with HttpFaultScript.Sequence(...) or HttpFaultScript.Always(...) " +
                "when more attempts are expected.");
        }

        try
        {
            var step = fault;
            if (step.Kind == HttpFaultKind.Delay)
            {
                await Task.Delay(step.DelayDuration!.Value, _timeProvider!, cancellationToken).ConfigureAwait(false);
                step = step.InnerFault!;
            }

            switch (step.Kind)
            {
                case HttpFaultKind.Response:
                    attempt.Complete(HttpAttemptOutcome.Response, step.StatusCode, step.RetryAfter);
                    return CreateResponse(step, request);

                case HttpFaultKind.NetworkError:
                    attempt.Complete(HttpAttemptOutcome.NetworkError);
                    throw new HttpRequestException(NetworkErrorMessage);

                default:
                    await WaitForCancellationAsync(cancellationToken).ConfigureAwait(false);
                    throw new OperationCanceledException(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            attempt.Complete(HttpAttemptOutcome.Abandoned);
            throw;
        }
    }

    private static HttpResponseMessage CreateResponse(HttpFault fault, HttpRequestMessage request)
    {
        var response = new HttpResponseMessage(fault.StatusCode) { RequestMessage = request };
        if (fault.RetryAfter is { } delta)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(delta);
        }

        return response;
    }

    private static async Task WaitForCancellationAsync(CancellationToken cancellationToken)
    {
        // No finite wait is involved: the attempt ends only when a timeout strategy or the caller cancels it.
        var neverCompletes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), neverCompletes))
        {
            await neverCompletes.Task.ConfigureAwait(false);
        }
    }
}
