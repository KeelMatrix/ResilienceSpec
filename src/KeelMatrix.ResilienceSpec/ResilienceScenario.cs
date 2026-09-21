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
    /// The tracking provider exposed by a <see cref="ResilienceScenarioClock"/> that the resilience pipeline must use,
    /// or <see langword="null"/> for a run that only asserts attempts and outcomes.
    /// </param>
    /// <param name="advanceTime">
    /// The operation that advances the wrapped provider, normally <c>clock.Advance</c>. It is required whenever a
    /// controllable clock is supplied.
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
                "new ResilienceScenario(script, clock.TimeProvider, clock.Advance), where clock is a ResilienceScenarioClock. " +
                "Timing assertions never fall back to the wall clock.");
        }

        if (timeProvider is not null && !ResilienceScenarioClock.IsTrackingProvider(timeProvider))
        {
            throw new MissingTimeProviderException(
                "Timing scenarios require a ResilienceScenarioClock so the scenario can detect timers released by " +
                "each virtual advance. Wrap the controllable provider and use the wrapper in both the scenario and " +
                "the client pipeline.");
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

        var logicalCall = Handler.Observer.BeginLogicalCall();
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var cleanupOwnsLease = false;
        var virtualElapsed = TimeSpan.Zero;
        Task<HttpResponseMessage> pending = null!;
        try
        {
            try
            {
                pending = client.SendAsync(request, linked.Token);
            }
            catch (Exception exception)
            {
                Handler.Observer.MarkSettled(TimeProvider is null ? null : virtualElapsed);
                return ResilienceResult.ForException(exception, cancellationToken.IsCancellationRequested, virtualElapsed, Handler.Report);
            }

            var settled = await CompleteWithinAsync(pending, Options.ObservationWindow).ConfigureAwait(false);

            if (!settled)
            {
                if (_advanceTime is not null && Options.AdvanceClock)
                {
                    while (!settled && virtualElapsed < Options.VirtualBudget)
                    {
                        var progressVersion = Handler.Observer.ProgressVersion;
                        var timerVersion = GetTimerCallbackVersion();
                        var remainingBudget = Options.VirtualBudget - virtualElapsed;
                        var nextTimerDue = GetNextTimerDue();
                        if (nextTimerDue is { } due && due <= TimeSpan.Zero)
                        {
                            var progressed = await Handler.Observer.WaitForProgressAsync(progressVersion, Options.ObservationWindow)
                                .ConfigureAwait(false);
                            settled = await CompleteWithinAsync(pending, Options.ObservationWindow).ConfigureAwait(false);
                            if (settled || !progressed)
                            {
                                break;
                            }

                            continue;
                        }

                        var advance = nextTimerDue is { } timer && timer < remainingBudget
                            ? timer
                            : remainingBudget < Options.AdvanceStep ? remainingBudget : Options.AdvanceStep;
                        _advanceTime(advance);
                        virtualElapsed += advance;

                        // A clock advance may release a retry timer whose continuation still has to schedule the next
                        // operation. A timer callback is the supported quiescence boundary: if it fired, wait for the
                        // continuation with the bounded watchdog. Ordinary advances with no fired timer yield once so
                        // asynchronous continuations can schedule their timers without paying a wall-clock wait.
                        var timerFired = HasTimerCallbackSince(timerVersion);
                        if (timerFired)
                        {
                            var progressed = await Handler.Observer.WaitForProgressAsync(progressVersion, Options.ObservationWindow)
                                .ConfigureAwait(false);
                            // A terminal strategy timeout can settle the client task without another scripted attempt.
                            // Observe that completion before applying the no-progress cutoff; only an unsettled request
                            // whose fired timer produced no downstream progress is unsafe to advance again.
                            settled = await CompleteWithinAsync(pending, Options.ObservationWindow).ConfigureAwait(false);
                            if (settled || !progressed)
                            {
                                break;
                            }
                        }
                        else
                        {
                            await Task.Yield();
                            settled = pending.IsCompleted;
                        }
                    }
                }
                else
                {
                    settled = await CompleteWithinAsync(pending, Options.PendingObservation).ConfigureAwait(false);
                }
            }

            if (!settled)
            {
                var cleanup = BeginCleanup(linked, pending);
                var cleanupCompleted = await CompleteWithinAsync(cleanup, Options.CleanupTimeout).ConfigureAwait(false);
                Handler.Observer.MarkObservationCutoff();
                if (cleanupCompleted)
                {
                    await cleanup.ConfigureAwait(false);
                }
                else
                {
                    // Keep the single-consumer lease and linked token alive until the late request and cancellation
                    // callbacks finish. A second logical call therefore fails clearly instead of sharing this
                    // scenario while abandoned work can still consume a script step or mutate its report.
                    cleanupOwnsLease = true;
                    _ = ReleaseLeaseAfterCleanupAsync(logicalCall, linked, cleanup);
                }

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
        catch
        {
            cleanupOwnsLease = await CleanupPendingAsync(logicalCall, linked, pending).ConfigureAwait(false);
            Handler.Observer.MarkObservationCutoff();
            throw;
        }
        finally
        {
            if (!cleanupOwnsLease)
            {
                logicalCall.Dispose();
                linked.Dispose();
            }
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

    private long GetTimerCallbackVersion() =>
        TimeProvider is { } provider && ResilienceScenarioClock.IsTrackingProvider(provider)
            ? ResilienceScenarioClock.GetTimerCallbackVersion(provider)
            : 0;

    private bool HasTimerCallbackSince(long version) =>
        GetTimerCallbackVersion() != version;

    private TimeSpan? GetNextTimerDue() =>
        TimeProvider is { } provider && ResilienceScenarioClock.IsTrackingProvider(provider)
            ? ResilienceScenarioClock.GetNextTimerDue(provider)
            : null;

    private async Task<bool> CleanupPendingAsync(
        LogicalCallScope logicalCall,
        CancellationTokenSource linked,
        Task<HttpResponseMessage> pending)
    {
        var cleanup = BeginCleanup(linked, pending);
        var cleanupCompleted = await CompleteWithinAsync(cleanup, Options.CleanupTimeout).ConfigureAwait(false);
        if (!cleanupCompleted)
        {
            _ = ReleaseLeaseAfterCleanupAsync(logicalCall, linked, cleanup);
            return true;
        }

        await cleanup.ConfigureAwait(false);
        return false;
    }

    private static Task BeginCleanup(
        CancellationTokenSource cancellation,
        Task<HttpResponseMessage> pending)
    {
        var cancel = ObserveCancellationAsync(cancellation);
        var request = ObserveResponseAsync(pending);
        return Task.WhenAll(cancel, request);
    }

    private static async Task ReleaseLeaseAfterCleanupAsync(
        LogicalCallScope logicalCall,
        CancellationTokenSource linked,
        Task cleanup)
    {
        try
        {
            await cleanup.ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Cleanup faults are already observed and cannot change the returned Pending result.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
        finally
        {
            logicalCall.Dispose();
            linked.Dispose();
        }
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
