namespace KeelMatrix.ResilienceSpec;

/// <summary>Describes how many in-flight requests one script may serve.</summary>
public enum ScriptConcurrency
{
    /// <summary>One script serves a single logical call. A concurrent second call fails with <see cref="ConcurrentScriptUseException"/>.</summary>
    SingleConsumer,

    /// <summary>One script may serve concurrent calls. Attempt ordinals then follow arrival order.</summary>
    AllowConcurrent,
}

/// <summary>Controls how a <see cref="ResilienceScenario"/> drives the injected clock and bounds its observation.</summary>
public sealed class ResilienceScenarioOptions
{
    /// <summary>Gets the default options.</summary>
    public static ResilienceScenarioOptions Default { get; } = new();

    /// <summary>
    /// Gets the amount of injected-clock time added for every observation step while a request is pending. The
    /// value is also the granularity reported by <see cref="HttpAttemptReport.ObservationStep"/> and used by the
    /// timing assertions. Defaults to 100 milliseconds.
    /// </summary>
    public TimeSpan AdvanceStep { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Gets the maximum amount of injected-clock time a scenario advances before it reports a pending result.
    /// Increase it when the configuration under test legitimately waits longer than this. Defaults to 15 seconds.
    /// </summary>
    public TimeSpan VirtualBudget { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Gets the wall-clock window in which the scenario waits for progress after each clock advance. This window
    /// only distinguishes "the request settled or produced an attempt" from "nothing happened yet"; it is never
    /// used as a timing measurement. It is also the window in which a pipeline continuation has to run after the
    /// clock advanced, so it must stay comfortably above ordinary thread-pool scheduling latency. The wall-clock
    /// cost of a run is the virtual budget divided by the advance step, multiplied by this window. Defaults to
    /// 50 milliseconds.
    /// </summary>
    public TimeSpan ObservationWindow { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Gets the wall-clock window used to watch a pending request when the scenario cannot advance the clock.
    /// Defaults to 1 second.
    /// </summary>
    public TimeSpan PendingObservation { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets the maximum wall-clock time spent waiting for cancellation callbacks and an abandoned request to
    /// cooperate before the scenario reports a pending observation cutoff. Defaults to 1 second.
    /// </summary>
    public TimeSpan CleanupTimeout { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets an optional callback that establishes pipeline quiescence after each clock advance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The callback receives the cumulative injected-clock time after the advance and must complete only after the
    /// resilience pipeline has finished progressing all continuations released by that advance. This is the supported
    /// coordination point for a pipeline adapter that deliberately delays a post-timer continuation. The scenario
    /// bounds the callback by <see cref="ObservationWindow"/>; a callback that does not complete produces an
    /// observation cutoff and the scenario never performs another advance.
    /// </para>
    /// <para>
    /// When no callback is supplied, the scenario uses the scripted downstream's progress signal and the configured
    /// observation window. That default is sufficient for ordinary handler chains; adapters with an explicit
    /// continuation or scheduler gate should supply this callback so the timing contract is fail-closed.
    /// </para>
    /// </remarks>
    public Func<TimeSpan, ValueTask>? WaitForPipelineProgress { get; init; }

    /// <summary>
    /// Gets a value indicating whether the scenario advances the injected clock. Set to <see langword="false"/>
    /// to observe a pending request without advancing time, which documents "clock not advanced, still pending".
    /// Defaults to <see langword="true"/>.
    /// </summary>
    public bool AdvanceClock { get; init; } = true;

    /// <summary>Gets the concurrency model of one script. Defaults to <see cref="ScriptConcurrency.SingleConsumer"/>.</summary>
    public ScriptConcurrency Concurrency { get; init; } = ScriptConcurrency.SingleConsumer;

    /// <summary>
    /// Gets a value indicating whether a scenario created with a controllable clock requires that clock to be
    /// registered as the <see cref="TimeProvider"/> of the dependency injection container. This guard prevents a
    /// timing assertion from silently observing a pipeline that still runs on the system clock. Defaults to
    /// <see langword="true"/>.
    /// </summary>
    public bool RequireRegisteredTimeProvider { get; init; } = true;

    internal ITelemetrySink TelemetrySink { get; init; } = SharedTelemetrySink.Instance;

    internal void Validate()
    {
        if (AdvanceStep <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(AdvanceStep), AdvanceStep, "An advance step must be greater than zero.");
        }

        if (VirtualBudget < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(VirtualBudget), VirtualBudget, "A virtual budget must not be negative.");
        }

        if (ObservationWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ObservationWindow), ObservationWindow, "An observation window must be greater than zero.");
        }

        if (PendingObservation <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(PendingObservation), PendingObservation, "A pending observation window must be greater than zero.");
        }

        if (CleanupTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(CleanupTimeout), CleanupTimeout, "A cleanup timeout must be greater than zero.");
        }
    }
}
