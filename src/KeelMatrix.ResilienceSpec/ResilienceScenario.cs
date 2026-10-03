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
/// A scenario owns exactly one logical call. Start it only with <see cref="SendAsync"/>; direct sends through
/// <see cref="Handler"/>, a manually constructed client, or a factory client fail with
/// <see cref="ScenarioConsumedException"/> before consuming a script step or changing <see cref="Report"/>.
/// Genuine retries and timeouts produced inside the configured handler chain inherit that logical-call lease. A
/// handler that deliberately clones a request must preserve the source request options, including the opaque
/// logical-call marker. The marker remains sufficient when execution-context flow is deliberately suppressed;
/// a fresh unmarked request created inside flowed handler context is rejected before script/report mutation.
/// Create one scenario per test case.
/// </para>
/// <para>
/// Telemetry policy: An activation is requested only after a settled scenario has either observed an injected failure
/// published by an executed attempt, an executed injected timeout classified as a timeout by the settled client or
/// strategy, or a positively recognized native HttpClient.Timeout outcome with at least one executed attempt, and an
/// assertion is evaluated at or after settlement. ResponseFault is true only for an HTTP response with status 400 or
/// higher published by an executed attempt. ExceptionFault is true only for a network failure published by an
/// executed attempt, an executed injected timeout classified as a timeout by the settled client or strategy, or a
/// positively recognized native HttpClient.Timeout outcome whose cancellation token is the same token passed to an
/// executed scripted attempt and is canceled. Plain caller cancellation, arbitrary upstream timeout exceptions
/// (including native-shaped exceptions without that token evidence), unexecuted script steps, and observation
/// cleanup do not set ExceptionFault. Failure categories accumulate across executed attempts: any case with a
/// published response or network category produces one signal containing the accumulated categories, while a case
/// with no category produces zero sink signals.
/// </para>
/// </remarks>
public sealed class ResilienceScenario : IDisposable
{
    private readonly ResilienceScenarioClock? _clock;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="ResilienceScenario"/> class.</summary>
    /// <param name="script">The script of downstream outcomes, one step per attempt.</param>
    /// <param name="clock">
    /// The scenario clock that owns the tracking provider and verified advance operation, or <see langword="null"/>
    /// for a run that only asserts attempts and outcomes.
    /// </param>
    /// <param name="options">Scenario options, or <see langword="null"/> for <see cref="ResilienceScenarioOptions.Default"/>.</param>
    public ResilienceScenario(
        HttpFaultScript script,
        ResilienceScenarioClock? clock = null,
        ResilienceScenarioOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(script);
        options ??= ResilienceScenarioOptions.Default;
        options.Validate();

        var timeProvider = clock?.TimeProvider;
        Script = script;
        TimeProvider = timeProvider;
        Options = options;
        _clock = clock;
        Handler = new ScriptedHttpMessageHandler(script, timeProvider, options);
    }

    /// <summary>Gets the script of downstream outcomes.</summary>
    public HttpFaultScript Script { get; }

    /// <summary>Gets the controllable clock of the scenario, or <see langword="null"/> when the scenario has none.</summary>
    public TimeProvider? TimeProvider { get; }

    /// <summary>Gets the options that control clock advancement and observation.</summary>
    public ResilienceScenarioOptions Options { get; }

    /// <summary>
    /// Gets the terminal handler that replaces the network boundary of the client under test. Install it in the
    /// configured chain, but start every operation with <see cref="SendAsync"/>.
    /// </summary>
    public ScriptedHttpMessageHandler Handler { get; }

    /// <summary>Gets a value indicating whether the scenario can assert timing behaviour.</summary>
    public bool SupportsTiming => TimeProvider is not null;

    /// <summary>Gets a snapshot of the attempts that reached the scripted downstream.</summary>
    public HttpAttemptReport Report => Handler.Report;

    /// <summary>
    /// Runs one request through the client under test while advancing the injected clock deterministically until the
    /// request settles or the virtual budget is exhausted. Legitimate timer-to-timer continuations are followed to
    /// their next tracked deadline, including a deadline exactly at the remaining budget; a deadline beyond the
    /// remaining budget remains an honest observation cutoff. Completed or disabled one-shot timers do not create
    /// phantom deadlines.
    /// </summary>
    /// <param name="client">The configured client whose real handler chain must stay in place.</param>
    /// <param name="request">The request to send.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>
    /// The outcome of the run, including the final response or exception and the attempt report. If observation or
    /// bounded cancellation cleanup expires first, the outcome is <see cref="ResilienceResultKind.Pending"/> and the
    /// report marks <see cref="HttpAttemptReport.IsObservationCutoff"/> rather than claiming request settlement. An
    /// attempt ended by that cleanup has no duration because cleanup is not timeout evidence. The initial handler
    /// invocation and each injected-clock advance are observed behind the same bounded watchdog, so synchronous user
    /// callbacks cannot prevent the observer from returning a cutoff; late work remains owned until cleanup completes.
    /// </returns>
    /// <exception cref="ScenarioConsumedException">The scenario has already been used for another logical call.</exception>
    public async Task<ResilienceResult> SendAsync(
        HttpClient client,
        HttpRequestMessage request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var logicalCall = Handler.Observer.BeginLogicalCall();
        using var execution = logicalCall.Enter(request);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var cleanupOwnsLease = false;
        var virtualElapsed = TimeSpan.Zero;
        Task<HttpResponseMessage> pending = null!;
        Task? pendingAdvance = null;
        try
        {
            try
            {
                // Run the initial handler invocation outside the observer so a synchronous user callback cannot
                // prevent the observation watchdog from starting. The logical-call token flows into the task and is
                // retired only after bounded cleanup has observed late completion.
                var invocationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                pending = Task.Factory.StartNew(
                        () =>
                        {
                            invocationStarted.TrySetResult();
                            return client.SendAsync(request, linked.Token);
                        },
                        CancellationToken.None,
                        TaskCreationOptions.DenyChildAttach | TaskCreationOptions.LongRunning,
                        TaskScheduler.Default)
                    .Unwrap();
                _ = invocationStarted.Task.Wait(Options.ObservationWindow, CancellationToken.None);
                _ = Handler.Observer.WaitForFirstAttemptAsync().Wait(Options.ObservationWindow, CancellationToken.None);
            }
            catch (TimingConfigurationException)
            {
                throw;
            }
            catch (Exception exception)
            {
                var callerCanceled = cancellationToken.IsCancellationRequested;
                var settledElapsed = Handler.Observer.MarkSettled(
                    ResilienceResult.IsTimeout(exception, callerCanceled),
                    !callerCanceled && Handler.Observer.HasNativeHttpClientTimeoutEvidence(exception)) ?? virtualElapsed;
                return ResilienceResult.ForException(exception, callerCanceled, settledElapsed, Handler.Report);
            }

            var settled = await CompleteWithinAsync(pending, Options.ObservationWindow).ConfigureAwait(false);

            if (!settled)
            {
                if (_clock is not null && Options.AdvanceClock)
                {
                    while (!settled && virtualElapsed < Options.VirtualBudget)
                    {
                        var progressVersion = Handler.Observer.ProgressVersion;
                        var timerVersion = GetTimerCallbackVersion();
                        var remainingBudget = Options.VirtualBudget - virtualElapsed;
                        var nextTimerDue = GetNextTimerDue();
                        if (nextTimerDue is null)
                        {
                            // A completed downstream attempt can resume the resilience continuation on another
                            // thread, which may register its retry timer just after the attempt publication. Observe
                            // that registration boundary before taking a fallback sample; otherwise the same run can
                            // be exact or sampled solely because of scheduler interleaving.
                            var scheduleVersion = GetTimerScheduleVersion();
                            nextTimerDue = GetNextTimerDue();
                            if (nextTimerDue is null)
                            {
                                var timerSchedule = WaitForTimerScheduleAsync(scheduleVersion);
                                var timerOrCompletion = Task.WhenAny(pending, timerSchedule);
                                var observed = await CompleteWithinAsync(timerOrCompletion, Options.ObservationWindow)
                                    .ConfigureAwait(false);
                                if (observed)
                                {
                                    settled = pending.IsCompleted;
                                    if (settled || timerSchedule.IsCompleted)
                                    {
                                        continue;
                                    }
                                }
                            }
                        }

                        if (nextTimerDue is { } due && due <= TimeSpan.Zero)
                        {
                            // A continuation can publish a zero-due timer after the preceding advance returns. Release
                            // that due timer through the scenario-controlled clock before waiting for callback
                            // dispatch; otherwise the fallback sample can wait on a callback that was never queued.
                            timerVersion = GetTimerCallbackVersion();
                            pendingAdvance = Task.Factory.StartNew(
                                () => _clock.Advance(TimeSpan.Zero),
                                CancellationToken.None,
                                TaskCreationOptions.DenyChildAttach | TaskCreationOptions.LongRunning,
                                TaskScheduler.Default);
                            var zeroAdvanceCompleted = await CompleteWithinAsync(
                                    pendingAdvance,
                                    Options.ObservationWindow)
                                .ConfigureAwait(false);
                            if (!zeroAdvanceCompleted)
                            {
                                break;
                            }

                            await pendingAdvance.ConfigureAwait(false);
                            pendingAdvance = null;

                            // FakeTimeProvider can queue a released timer callback after Advance returns. Wait for
                            // callback dispatch before sampling observer progress; otherwise a slow host can report
                            // Pending while the due timer is already in flight.
                            var timerCallbackCompleted = await CompleteWithinAsync(
                                _clock.WaitForTimerCallbackAsync(timerVersion),
                                Options.ObservationWindow).ConfigureAwait(false);
                            if (!timerCallbackCompleted)
                            {
                                break;
                            }

                            var progressed = await Handler.Observer.WaitForProgressAsync(
                                    progressVersion,
                                    Options.ObservationWindow,
                                    () => GetNextTimerDue() is not null)
                                .ConfigureAwait(false);
                            settled = await CompleteWithinAsync(pending, Options.ObservationWindow).ConfigureAwait(false);
                            if (settled || !progressed)
                            {
                                break;
                            }

                            continue;
                        }

                        var advance = nextTimerDue is { } timer && timer <= remainingBudget
                            ? timer
                            : remainingBudget < Options.AdvanceStep ? remainingBudget : Options.AdvanceStep;
                        var targetsTimerDeadline = nextTimerDue is { } target && target == advance;
                        pendingAdvance = Task.Factory.StartNew(
                            () => _clock.Advance(advance),
                            CancellationToken.None,
                            TaskCreationOptions.DenyChildAttach | TaskCreationOptions.LongRunning,
                            TaskScheduler.Default);
                        var advanceCompleted = await CompleteWithinAsync(
                                pendingAdvance,
                                Options.ObservationWindow)
                            .ConfigureAwait(false);
                        if (!advanceCompleted)
                        {
                            break;
                        }

                        await pendingAdvance.ConfigureAwait(false);
                        pendingAdvance = null;
                        virtualElapsed += advance;

                        if (targetsTimerDeadline)
                        {
                            var timerCallbackCompleted = await CompleteWithinAsync(
                                _clock.WaitForTimerCallbackAsync(timerVersion),
                                Options.ObservationWindow).ConfigureAwait(false);
                            if (!timerCallbackCompleted)
                            {
                                break;
                            }
                        }

                        // A clock advance may release a retry timer whose continuation still has to schedule the next
                        // operation. A timer callback is the supported quiescence boundary: if it fired, wait for the
                        // continuation with the bounded watchdog. Ordinary advances with no fired timer yield once so
                        // asynchronous continuations can schedule their timers without paying a wall-clock wait.
                        var timerFired = HasTimerCallbackSince(timerVersion);
                        if (timerFired)
                        {
                            var progress = Handler.Observer.WaitForProgressAsync(
                                    progressVersion,
                                    Options.ObservationWindow,
                                    () => GetNextTimerDue() is not null);
                            var scheduleVersion = GetTimerScheduleVersion();
                            var timerSchedule = WaitForTimerScheduleAsync(scheduleVersion);
                            var progressOrSchedule = Task.WhenAny(progress, timerSchedule, pending);
                            var observedProgress = await progressOrSchedule.ConfigureAwait(false);

                            // A terminal strategy timeout can settle the client task without another scripted attempt.
                            // Observe that completion before applying the no-progress cutoff; only an unsettled request
                            // whose fired timer produced no downstream progress or timer publication is unsafe to
                            // advance again.
                            settled = await CompleteWithinAsync(pending, Options.ObservationWindow).ConfigureAwait(false);
                            if (settled)
                            {
                                break;
                            }

                            if (ReferenceEquals(observedProgress, progress))
                            {
                                if (!await progress.ConfigureAwait(false))
                                {
                                    break;
                                }
                            }
                            else if (ReferenceEquals(observedProgress, pending))
                            {
                                settled = true;
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
                Handler.Observer.MarkObservationCleanupStarted();
                var cleanup = BeginCleanup(linked, pending, pendingAdvance);
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
                var settledElapsed = Handler.Observer.MarkSettled() ?? virtualElapsed;
                return ResilienceResult.ForResponse(response, settledElapsed, Handler.Report);
            }
            catch (TimingConfigurationException)
            {
                throw;
            }
            catch (Exception exception)
            {
                var callerCanceled = cancellationToken.IsCancellationRequested;
                var settledElapsed = Handler.Observer.MarkSettled(
                    ResilienceResult.IsTimeout(exception, callerCanceled),
                    !callerCanceled && Handler.Observer.HasNativeHttpClientTimeoutEvidence(exception)) ?? virtualElapsed;
                return ResilienceResult.ForException(exception, callerCanceled, settledElapsed, Handler.Report);
            }
        }
        catch
        {
            Handler.Observer.MarkObservationCleanupStarted();
            cleanupOwnsLease = await CleanupPendingAsync(logicalCall, linked, pending, pendingAdvance).ConfigureAwait(false);
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
        return ReferenceEquals(completed, task) || task.IsCompleted;
    }

    private long GetTimerCallbackVersion() =>
        TimeProvider is { } provider && ResilienceScenarioClock.IsTrackingProvider(provider)
            ? ResilienceScenarioClock.GetTimerCallbackVersion(provider)
            : 0;

    private long GetTimerScheduleVersion() =>
        TimeProvider is { } provider && ResilienceScenarioClock.IsTrackingProvider(provider)
            ? ResilienceScenarioClock.GetTimerScheduleVersion(provider)
            : 0;

    private Task WaitForTimerScheduleAsync(long version) =>
        TimeProvider is { } provider && ResilienceScenarioClock.IsTrackingProvider(provider)
            ? ((ResilienceScenarioClock)_clock!).WaitForTimerScheduleAsync(version)
            : Task.CompletedTask;

    private bool HasTimerCallbackSince(long version) =>
        GetTimerCallbackVersion() != version;

    private TimeSpan? GetNextTimerDue() =>
        TimeProvider is { } provider && ResilienceScenarioClock.IsTrackingProvider(provider)
            ? ResilienceScenarioClock.GetNextTimerDue(provider)
            : null;

    private async Task<bool> CleanupPendingAsync(
        LogicalCallScope logicalCall,
        CancellationTokenSource linked,
        Task<HttpResponseMessage> pending,
        Task? pendingAdvance)
    {
        var cleanup = BeginCleanup(linked, pending, pendingAdvance);
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
        Task<HttpResponseMessage> pending,
        Task? pendingAdvance)
    {
        var cancel = ObserveCancellationAsync(cancellation);
        var request = ObserveResponseAsync(pending);
        var advance = pendingAdvance is null ? Task.CompletedTask : ObserveTaskAsync(pendingAdvance);
        return Task.WhenAll(cancel, request, advance);
    }

    private static async Task ObserveTaskAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Late advance faults are observed so a bounded Pending result remains stable.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
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
