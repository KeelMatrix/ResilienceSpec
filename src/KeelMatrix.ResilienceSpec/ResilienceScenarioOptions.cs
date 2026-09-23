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
    /// Gets the fallback amount of injected-clock time added while a request is pending when the tracking clock has no
    /// scheduled timer to target. The value is reported by <see cref="HttpAttemptReport.ObservationStep"/> as the
    /// sampling interval. Exact timing assertions fail closed after fallback sampling; this value is never an implicit
    /// tolerance. When a supported tracking clock exposes a timer deadline, the scenario advances directly to that
    /// deadline. Defaults to 100 milliseconds.
    /// </summary>
    public TimeSpan AdvanceStep { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Gets the maximum amount of injected-clock time a scenario advances before it reports a pending result.
    /// Increase it when the configuration under test legitimately waits longer than this. Defaults to 15 seconds.
    /// </summary>
    public TimeSpan VirtualBudget { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Gets the bounded wall-clock watchdog used after a provider timer fires and its continuation must reach the
    /// scripted downstream. It only distinguishes "the request settled or made progress" from "the pipeline is
    /// stalled"; it is never used as a timing measurement. Ordinary virtual advances with no fired timer do not wait
    /// for this window. Defaults to 50 milliseconds.
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

        ValidateTaskDelayWindow(nameof(ObservationWindow), ObservationWindow);

        if (PendingObservation <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(PendingObservation), PendingObservation, "A pending observation window must be greater than zero.");
        }

        ValidateTaskDelayWindow(nameof(PendingObservation), PendingObservation);

        if (CleanupTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(CleanupTimeout), CleanupTimeout, "A cleanup timeout must be greater than zero.");
        }

        ValidateTaskDelayWindow(nameof(CleanupTimeout), CleanupTimeout);
    }

    private static void ValidateTaskDelayWindow(string name, TimeSpan value)
    {
        if (value > TimeSpan.FromMilliseconds(int.MaxValue))
        {
            throw new ArgumentOutOfRangeException(
                name,
                value,
                "The observation window exceeds the maximum duration supported by Task.Delay.");
        }
    }
}
