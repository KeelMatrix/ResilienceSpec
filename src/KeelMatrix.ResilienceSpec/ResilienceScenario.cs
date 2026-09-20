namespace KeelMatrix.ResilienceSpec;

/// <summary>
/// Drives one scripted downstream through the client under test and advances the injected clock deterministically
/// while the request is pending.
/// </summary>
/// <remarks>
/// <para>
/// The scenario owns the terminal handler (<see cref="Handler"/>) that replaces the network boundary, the clock
/// the resilience pipeline must use, and the attempt report. Install the handler on the client under test — for
/// example with <see cref="ResilienceSpecHttpClientBuilderExtensions.UseResilienceSpecDownstream"/> — and run
/// requests through <see cref="SendAsync"/> so that retries, delays, and timeouts happen on the injected clock
/// instead of on the wall clock.
/// </para>
/// <para>
/// A scenario is deterministic for a single logical call. Create one scenario per test case.
/// </para>
/// </remarks>
public sealed class ResilienceScenario : IDisposable
{
    private readonly Action<TimeSpan>? _advanceTime;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="ResilienceScenario"/> class.</summary>
    /// <param name="script">The script of downstream outcomes, one step per attempt.</param>
    /// <param name="timeProvider">
    /// The controllable clock that the resilience pipeline must use, or <see langword="null"/> for a run that only
    /// asserts attempts and outcomes.
    /// </param>
    /// <param name="advanceTime">
    /// The operation that advances <paramref name="timeProvider"/>, for example <c>clock.Advance</c>. It is required
    /// whenever a controllable clock is supplied.
    /// </param>
    /// <param name="options">Scenario options, or <see langword="null"/> for <see cref="ResilienceScenarioOptions.Default"/>.</param>
    public ResilienceScenario(
        HttpFaultScript script,
        TimeProvider? timeProvider = null,
        Action<TimeSpan>? advanceTime = null,
        ResilienceScenarioOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(script);
        options ??= ResilienceScenarioOptions.Default;
        options.Validate();

        if (timeProvider is null && advanceTime is not null)
        {
            throw new ArgumentException(
                "An advance operation requires the controllable clock it advances.",
                nameof(advanceTime));
        }

        if (timeProvider is not null && advanceTime is null)
        {
            throw new MissingTimeProviderException(
                "A controllable clock must be supplied together with the operation that advances it, for example " +
                "new ResilienceScenario(script, clock, clock.Advance). Timing assertions never fall back to the wall clock.");
        }

        Script = script;
        TimeProvider = timeProvider;
        Options = options;
        _advanceTime = advanceTime;
        Handler = new ScriptedHttpMessageHandler(script, timeProvider, options);
    }

    /// <summary>Gets the script of downstream outcomes.</summary>
    public HttpFaultScript Script { get; }

    /// <summary>Gets the controllable clock of the scenario, or <see langword="null"/> when the scenario has none.</summary>
    public TimeProvider? TimeProvider { get; }

    /// <summary>Gets the options that control clock advancement and observation.</summary>
    public ResilienceScenarioOptions Options { get; }

    /// <summary>Gets the terminal handler that replaces the network boundary of the client under test.</summary>
    public ScriptedHttpMessageHandler Handler { get; }

    /// <summary>Gets a value indicating whether the scenario can assert timing behaviour.</summary>
    public bool SupportsTiming => TimeProvider is not null;

    /// <summary>Gets a snapshot of the attempts that reached the scripted downstream.</summary>
    public HttpAttemptReport Report => Handler.Report;

    /// <summary>
    /// Runs one request through the client under test while advancing the injected clock deterministically until the
    /// request settles or the virtual budget is exhausted.
    /// </summary>
    /// <param name="client">The configured client whose real handler chain must stay in place.</param>
    /// <param name="request">The request to send.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>
    /// The outcome of the run, including the final response or exception and the attempt report. If observation or
    /// bounded cancellation cleanup expires first, the outcome is <see cref="ResilienceResultKind.Pending"/> and the
    /// report marks <see cref="HttpAttemptReport.IsObservationCutoff"/> rather than claiming request settlement.
    /// </returns>
    public async Task<ResilienceResult> SendAsync(
        HttpClient client,
        HttpRequestMessage request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var logicalCall = Handler.Observer.BeginLogicalCall();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = client.SendAsync(request, linked.Token);
        var virtualElapsed = TimeSpan.Zero;
        var settled = await CompleteWithinAsync(pending, Options.ObservationWindow).ConfigureAwait(false);

        if (!settled)
        {
            if (_advanceTime is not null && Options.AdvanceClock)
            {
                while (!settled && virtualElapsed < Options.VirtualBudget)
                {
                    var progressVersion = Handler.Observer.ProgressVersion;
                    var remainingBudget = Options.VirtualBudget - virtualElapsed;
                    var advance = remainingBudget < Options.AdvanceStep ? remainingBudget : Options.AdvanceStep;
                    _advanceTime(advance);
                    virtualElapsed += advance;

                    // A clock advance may release a retry timer whose continuation still has to schedule the next
                    // operation. First honor the explicit adapter quiescence contract, when supplied. If it does not
                    // complete within the observation window, stop honestly instead of advancing past a continuation
                    // that has not finished progressing.
                    var quiescent = await WaitForPipelineProgressAsync(virtualElapsed).ConfigureAwait(false);
                    if (!quiescent)
                    {
                        break;
                    }

                    // The terminal handler's progress signal covers ordinary handler chains. It is deliberately
                    // bounded: a pipeline that neither settles nor reaches the scripted downstream remains pending.
                    await Handler.Observer.WaitForProgressAsync(progressVersion, Options.ObservationWindow).ConfigureAwait(false);
                    settled = await CompleteWithinAsync(pending, Options.ObservationWindow).ConfigureAwait(false);
                }
            }
            else
            {
                settled = await CompleteWithinAsync(pending, Options.PendingObservation).ConfigureAwait(false);
            }
        }

        if (!settled)
        {
            await BoundCleanupAsync(linked, pending, Options.CleanupTimeout).ConfigureAwait(false);
            Handler.Observer.MarkObservationCutoff();
            return ResilienceResult.Pending(virtualElapsed, Handler.Report);
        }

        try
        {
            var response = await pending.ConfigureAwait(false);
            Handler.Observer.MarkSettled(TimeProvider is null ? null : virtualElapsed);
            return ResilienceResult.ForResponse(response, virtualElapsed, Handler.Report);
        }
        catch (Exception exception)
        {
            Handler.Observer.MarkSettled(TimeProvider is null ? null : virtualElapsed);
            return ResilienceResult.ForException(exception, cancellationToken.IsCancellationRequested, virtualElapsed, Handler.Report);
        }
    }

    /// <summary>Disposes the scripted terminal handler.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Handler.Dispose();
    }

    /// <summary>Records that this scenario is consumed through the HttpClientFactory adapter.</summary>
    internal void MarkHttpClientFactoryIntegration() =>
        Handler.Observer.Telemetry.MarkIntegrationPath(IntegrationPath.HttpClientFactory);

    private static async Task<bool> CompleteWithinAsync(Task task, TimeSpan window)
    {
        if (task.IsCompleted)
        {
            return true;
        }

        var completed = await Task.WhenAny(task, Task.Delay(window)).ConfigureAwait(false);
        return ReferenceEquals(completed, task);
    }

    private async Task<bool> WaitForPipelineProgressAsync(TimeSpan virtualElapsed)
    {
        if (Options.WaitForPipelineProgress is not { } callback)
        {
            return true;
        }

        var callbackTask = callback(virtualElapsed).AsTask();
        var completed = await Task.WhenAny(callbackTask, Task.Delay(Options.ObservationWindow)).ConfigureAwait(false);
        if (ReferenceEquals(completed, callbackTask))
        {
            await callbackTask.ConfigureAwait(false);
            return true;
        }

        // Do not leave a late callback fault unobserved after returning an honest cutoff.
        _ = ObserveCallbackAsync(callbackTask);
        return false;
    }

    private static async Task ObserveCallbackAsync(Task callback)
    {
        try
        {
            await callback.ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A late adapter callback is observed after the scenario returned Pending.
        catch (Exception)
#pragma warning restore CA1031
        {
            // The callback's late failure cannot change the already returned observation cutoff.
        }
    }

    private static async Task BoundCleanupAsync(
        CancellationTokenSource cancellation,
        Task<HttpResponseMessage> pending,
        TimeSpan timeout)
    {
        var cancel = ObserveCancellationAsync(cancellation);
        var request = ObserveResponseAsync(pending);
        var cleanup = Task.WhenAll(cancel, request);
        var completed = await Task.WhenAny(cleanup, Task.Delay(timeout)).ConfigureAwait(false);
        if (ReferenceEquals(completed, cleanup))
        {
            await cleanup.ConfigureAwait(false);
            return;
        }

        // The caller gets an honest Pending result at the cleanup deadline. The abandoned work is still observed in
        // the background so a late exception is not unobserved and a late response is not leaked.
        _ = cleanup;
    }

    private static async Task ObserveCancellationAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The pending request was cancelled by this scenario; its outcome is deliberately not surfaced.
        catch (Exception)
#pragma warning restore CA1031
        {
            // Cancellation cleanup is best effort and is bounded by the caller's cleanup deadline.
        }
    }

    private static async Task ObserveResponseAsync(Task<HttpResponseMessage> pending)
    {
        try
        {
            var response = await pending.ConfigureAwait(false);
            response.Dispose();
        }
#pragma warning disable CA1031 // Late cleanup is observed so it cannot become an unobserved task fault.
        catch (Exception)
#pragma warning restore CA1031
        {
            // The caller already received Pending; late faults are deliberately observed and not rethrown.
        }
    }
}
