namespace KeelMatrix.ResilienceSpec;

/// <summary>
/// Wraps a controllable <see cref="TimeProvider"/> and records timers that fire during a virtual-time advance.
/// </summary>
/// <remarks>
/// <para>
/// Use this clock both in the client under test and in <see cref="ResilienceScenario"/>. The wrapped provider remains
/// responsible for virtual time; the wrapper adds the progress signal needed to distinguish an ordinary intermediate
/// delay from a timer that fired but whose continuation has not reached the scripted downstream yet.
/// </para>
/// <para>
/// The wrapped provider's advance operation must synchronously dispatch the timers released by an advance. This is the
/// contract of the supported controllable providers used with the package, including
/// <c>Microsoft.Extensions.TimeProvider.Testing.FakeTimeProvider</c>.
/// </para>
/// </remarks>
public sealed class ResilienceScenarioClock
{
    private readonly TrackingTimeProvider _provider;
    private readonly Action<TimeSpan> _advanceInner;

    /// <summary>Initializes a clock wrapper around a controllable provider.</summary>
    /// <param name="inner">The controllable provider that owns the virtual time.</param>
    /// <param name="advanceInner">The operation that advances <paramref name="inner"/>.</param>
    public ResilienceScenarioClock(TimeProvider inner, Action<TimeSpan> advanceInner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(advanceInner);

        if (inner is TrackingTimeProvider)
        {
            throw new ArgumentException("The wrapped provider must be the underlying controllable clock.", nameof(inner));
        }

        _provider = new TrackingTimeProvider(inner);
        _advanceInner = advanceInner;
    }

    /// <summary>Gets the tracking provider to register in the client pipeline.</summary>
    public TimeProvider TimeProvider => _provider;

    /// <summary>Advances the wrapped controllable provider.</summary>
    /// <param name="amount">The non-negative virtual duration to advance.</param>
    public void Advance(TimeSpan amount)
    {
        if (amount < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "A clock advance must not be negative.");
        }

        _advanceInner(amount);
    }

#pragma warning disable RS0016 // PublicApiAnalyzers does not represent conversion operators in this baseline format.
    /// <summary>Converts the wrapper to the tracking provider used by the client pipeline.</summary>
    public static implicit operator TimeProvider(ResilienceScenarioClock clock) =>
        clock?.TimeProvider ?? throw new ArgumentNullException(nameof(clock));
#pragma warning restore RS0016

    internal static bool IsTrackingProvider(TimeProvider provider) => provider is TrackingTimeProvider;

    internal static long GetTimerCallbackVersion(TimeProvider provider) =>
        ((TrackingTimeProvider)provider).TimerCallbackVersion;

    private sealed class TrackingTimeProvider : TimeProvider
    {
        private readonly TimeProvider _inner;
        private long _timerCallbackVersion;

        internal TrackingTimeProvider(TimeProvider inner) => _inner = inner;

        internal long TimerCallbackVersion => Interlocked.Read(ref _timerCallbackVersion);

        internal void MarkTimerCallback() => Interlocked.Increment(ref _timerCallbackVersion);

        public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();

        public override long GetTimestamp() => _inner.GetTimestamp();

        public override long TimestampFrequency => _inner.TimestampFrequency;

        public override TimeZoneInfo LocalTimeZone => _inner.LocalTimeZone;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            ArgumentNullException.ThrowIfNull(callback);

            var timer = new TrackedTimer(this, callback, state);
            timer.InnerTimer = _inner.CreateTimer(timer.Invoke, null, dueTime, period);
            return timer;
        }
    }

    private sealed class TrackedTimer : ITimer
    {
        private readonly TrackingTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;

        internal TrackedTimer(TrackingTimeProvider owner, TimerCallback callback, object? state)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
        }

        internal ITimer? InnerTimer { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period) =>
            InnerTimer?.Change(dueTime, period) ?? false;

        public void Dispose() => InnerTimer?.Dispose();

        public ValueTask DisposeAsync() =>
            InnerTimer is { } timer ? timer.DisposeAsync() : ValueTask.CompletedTask;

        internal void Invoke(object? _)
        {
            _owner.MarkTimerCallback();
            _callback(_state);
        }
    }
}
