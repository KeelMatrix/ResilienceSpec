namespace KeelMatrix.ResilienceSpec;

/// <summary>
/// Wraps the supported <c>Microsoft.Extensions.Time.Testing.FakeTimeProvider</c> and records timers that fire during
/// a virtual-time advance.
/// </summary>
/// <remarks>
/// <para>
/// Use this clock both in the client under test and in <see cref="ResilienceScenario"/>. The wrapped provider remains
/// responsible for virtual time; the wrapper adds the progress signal needed to distinguish an ordinary intermediate
/// delay from a timer that fired but whose continuation has not reached the scripted downstream yet.
/// </para>
/// <para>
/// The wrapped provider's advance operation must synchronously dispatch the timers released by an advance. The package
/// admits only the runtime <see cref="Type"/> identity resolved from the strong-named
/// <c>Microsoft.Extensions.TimeProvider.Testing</c> assembly. Derived, delegating, and name-spoofed consumer types are
/// rejected because they cannot prove that their time source is the supported framework fake.
/// </para>
/// </remarks>
public sealed class ResilienceScenarioClock
{
    private readonly TrackingTimeProvider _provider;
    private readonly Action<TimeSpan> _advanceInner;

    /// <summary>Initializes a clock wrapper around a controllable provider.</summary>
    /// <param name="inner">The controllable provider that owns the virtual time.</param>
    /// <param name="advanceInner">The operation that advances <paramref name="inner"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="inner"/> is <see cref="TimeProvider.System"/>, is not the exact runtime type identity of the supported <c>FakeTimeProvider</c>, or is already tracking another clock.</exception>
    public ResilienceScenarioClock(TimeProvider inner, Action<TimeSpan> advanceInner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(advanceInner);

        if (inner is TrackingTimeProvider)
        {
            throw new ArgumentException("The wrapped provider must be the underlying controllable clock.", nameof(inner));
        }

        if (ReferenceEquals(inner, TimeProvider.System))
        {
            throw new ArgumentException(
                "TimeProvider.System is a wall-clock provider and cannot be wrapped as a controllable clock. " +
                "Use Microsoft.Extensions.Time.Testing.FakeTimeProvider.",
                nameof(inner));
        }

        if (!IsSupportedControllableProvider(inner))
        {
            throw new ArgumentException(
                "Timing scenarios require the exact runtime identity of Microsoft.Extensions.Time.Testing.FakeTimeProvider. " +
                "Consumer-authored derived, delegating, or name-spoofed TimeProvider types cannot prove deterministic timing.",
                nameof(inner));
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

    internal static bool IsTrackingProvider(TimeProvider provider) => provider is TrackingTimeProvider;

    private static readonly Type? SupportedControllableProviderType = ResolveSupportedControllableProviderType();

    private static bool IsSupportedControllableProvider(TimeProvider provider) =>
        SupportedControllableProviderType is { } supportedType && provider.GetType() == supportedType;

    private static Type? ResolveSupportedControllableProviderType()
    {
        try
        {
            return Type.GetType(
                "Microsoft.Extensions.Time.Testing.FakeTimeProvider, Microsoft.Extensions.TimeProvider.Testing, " +
                "PublicKeyToken=31bf3856ad364e35",
                throwOnError: false,
                ignoreCase: false);
        }
        catch (FileLoadException)
        {
            return null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    internal static long GetTimerCallbackVersion(TimeProvider provider) =>
        ((TrackingTimeProvider)provider).TimerCallbackVersion;

    internal static TimeSpan? GetNextTimerDue(TimeProvider provider) =>
        ((TrackingTimeProvider)provider).NextTimerDue;

    private sealed class TrackingTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly TimeProvider _inner;
        private readonly List<TrackedTimer> _timers = new();
        private long _timerCallbackVersion;

        internal TrackingTimeProvider(TimeProvider inner) => _inner = inner;

        internal long TimerCallbackVersion => Interlocked.Read(ref _timerCallbackVersion);

        internal TimeSpan? NextTimerDue
        {
            get
            {
                lock (_gate)
                {
                    var now = _inner.GetTimestamp();
                    TimeSpan? next = null;
                    foreach (var timer in _timers)
                    {
                        if (timer.DueTimestamp is not { } due)
                        {
                            continue;
                        }

                        var remaining = due <= now ? TimeSpan.Zero : _inner.GetElapsedTime(now, due);
                        if (next is null || remaining < next.Value)
                        {
                            next = remaining;
                        }
                    }

                    return next;
                }
            }
        }

        internal void MarkTimerCallback(TrackedTimer timer)
        {
            lock (_gate)
            {
                if (timer.Period == Timeout.InfiniteTimeSpan)
                {
                    timer.DueTimestamp = null;
                }
                else
                {
                    timer.DueTimestamp = TimestampAfter(_inner.GetTimestamp(), timer.Period);
                }

                _timerCallbackVersion++;
            }
        }

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
            timer.SetSchedule(dueTime, period);
            lock (_gate)
            {
                _timers.Add(timer);
            }

            try
            {
                timer.InnerTimer = _inner.CreateTimer(timer.Invoke, null, dueTime, period);
            }
            catch
            {
                lock (_gate)
                {
                    _timers.Remove(timer);
                }

                throw;
            }

            return timer;
        }

        internal void ChangeTimer(TrackedTimer timer, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                timer.SetSchedule(dueTime, period);
            }
        }

        internal void RemoveTimer(TrackedTimer timer)
        {
            lock (_gate)
            {
                _timers.Remove(timer);
            }
        }

        internal long? TimestampAfterForTimer(TimeSpan duration) =>
            TimestampAfter(_inner.GetTimestamp(), duration);

        private long? TimestampAfter(long start, TimeSpan duration)
        {
            if (duration == Timeout.InfiniteTimeSpan)
            {
                return null;
            }

            var normalized = duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
            var delta = (decimal)normalized.Ticks * _inner.TimestampFrequency / TimeSpan.TicksPerSecond;
            if (delta >= long.MaxValue)
            {
                return long.MaxValue;
            }

            var deltaTimestamp = (long)Math.Ceiling(delta);
            return start > long.MaxValue - deltaTimestamp ? long.MaxValue : start + deltaTimestamp;
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

        internal long? DueTimestamp { get; set; }

        internal TimeSpan Period { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            var changed = InnerTimer?.Change(dueTime, period) ?? false;
            if (changed)
            {
                _owner.ChangeTimer(this, dueTime, period);
            }

            return changed;
        }

        public void Dispose()
        {
            _owner.RemoveTimer(this);
            InnerTimer?.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            _owner.RemoveTimer(this);
            if (InnerTimer is { } timer)
            {
                await timer.DisposeAsync().ConfigureAwait(false);
            }
        }

        internal void Invoke(object? _)
        {
            _owner.MarkTimerCallback(this);
            _callback(_state);
        }

        internal void SetSchedule(TimeSpan dueTime, TimeSpan period)
        {
            Period = period;
            DueTimestamp = _owner.TimestampAfterForTimer(dueTime);
        }
    }
}
