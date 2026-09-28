namespace KeelMatrix.ResilienceSpec;

/// <summary>
/// Wraps a controllable <see cref="TimeProvider"/>, verifies scenario-controlled time movement, and records timers
/// that fire during a virtual-time advance.
/// </summary>
/// <remarks>
/// <para>
/// Use this clock both in the client under test and in <see cref="ResilienceScenario"/>. The wrapped provider remains
/// responsible for virtual time; the wrapper adds the progress signal needed to distinguish an ordinary intermediate
/// delay from a timer that fired but whose continuation has not reached the scripted downstream yet.
/// </para>
/// <para>
/// The advance operation must move the wrapped provider by exactly the requested duration and synchronously dispatch
/// timers released by the advance. Movement observed outside that operation is rejected. The package uses only the
/// public <see cref="TimeProvider"/> contract; it does not inspect provider implementation details. If an external
/// actor mutates the underlying provider during an advance and the final public timestamp has the same net delta,
/// the public contract cannot identify that actor, so exclusive ownership of the underlying provider remains the
/// caller's responsibility.
/// </para>
/// </remarks>
public sealed class ResilienceScenarioClock
{
    private readonly ProviderController _controller;
    private readonly TrackingTimeProvider _provider;
    private readonly Action<TimeSpan> _advanceInner;

    /// <summary>Initializes a clock wrapper around a controllable provider.</summary>
    /// <param name="inner">The controllable provider that owns the virtual time.</param>
    /// <param name="advanceInner">The operation that advances <paramref name="inner"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="inner"/> is <see cref="TimeProvider.System"/> or is already tracking another clock.</exception>
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

        _controller = new ProviderController(inner);
        _provider = new TrackingTimeProvider(inner, _controller);
        _advanceInner = advanceInner;
    }

    /// <summary>Gets the tracking provider to register in the client pipeline.</summary>
    public TimeProvider TimeProvider => _provider;

    /// <summary>Advances the wrapped provider and verifies that it moved by exactly the requested duration.</summary>
    /// <param name="amount">The non-negative virtual duration to advance.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="amount"/> is negative.</exception>
    /// <exception cref="InvalidOperationException">The provider configuration changed, the provider moved outside this method, or the configured operation did not advance the admitted provider by exactly <paramref name="amount"/>.</exception>
    public void Advance(TimeSpan amount)
    {
        if (amount < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "A clock advance must not be negative.");
        }

        var targetsKnownDeadline = _provider.NextTimerDue is { } due && due == amount;
        var before = _controller.BeginAdvance(targetsKnownDeadline || amount == TimeSpan.Zero);
        try
        {
            _advanceInner(amount);
        }
        catch
        {
            _controller.CancelAdvance(before);
            throw;
        }

        _controller.CompleteAdvance(before, amount);
    }

    internal static bool IsTrackingProvider(TimeProvider provider) => provider is TrackingTimeProvider;

    internal Task WaitForTimerCallbackAsync(long observedVersion) =>
        _provider.WaitForTimerCallbackAsync(observedVersion);

    internal static long GetTimerCallbackVersion(TimeProvider provider) =>
        ((TrackingTimeProvider)provider).TimerCallbackVersion;

    internal static long GetTimerScheduleVersion(TimeProvider provider) =>
        ((TrackingTimeProvider)provider).TimerScheduleVersion;

    internal static TimeSpan? GetNextTimerDue(TimeProvider provider) =>
        ((TrackingTimeProvider)provider).NextTimerDue;

    internal Task WaitForTimerScheduleAsync(long observedVersion) =>
        _provider.WaitForTimerScheduleAsync(observedVersion);

    internal static bool HasExactTimingEvidence(TimeProvider provider) =>
        ((TrackingTimeProvider)provider).HasExactTimingEvidence;

    internal static void BeginLogicalCall(TimeProvider provider) =>
        ((TrackingTimeProvider)provider).BeginLogicalCall();

    private sealed class ProviderController
    {
        private readonly object _gate = new();
        private readonly TimeProvider _inner;
        private long _lastVerifiedTimestamp;
        private bool _advanceInProgress;
        private bool _hasExactTimingEvidence = true;
        private bool _timerCallbackMovementViolation;
        private readonly AsyncLocal<int> _timerCallbackDepth = new();
        private readonly AsyncLocal<long?> _timerCallbackStartTimestamp = new();

        internal ProviderController(TimeProvider inner)
        {
            _inner = inner;
            _lastVerifiedTimestamp = inner.GetTimestamp();
        }

        internal bool HasExactTimingEvidence
        {
            get
            {
                lock (_gate)
                {
                    ValidateUnexpectedMovement();
                    return _hasExactTimingEvidence;
                }
            }
        }

        internal long BeginAdvance(bool producesExactEvidence)
        {
            lock (_gate)
            {
                ValidateUnexpectedMovement();
                if (_advanceInProgress)
                {
                    throw new TimingConfigurationException(
                        "The scenario attempted to advance its controllable provider recursively. " +
                        "Use one scenario-controlled advance operation per virtual-time step.");
                }

                _advanceInProgress = true;
                _hasExactTimingEvidence &= producesExactEvidence;
                _timerCallbackMovementViolation = false;
                return _lastVerifiedTimestamp;
            }
        }

        internal void BeginLogicalCall()
        {
            lock (_gate)
            {
                ValidateUnexpectedMovement();
                _hasExactTimingEvidence = true;
            }
        }

        internal void CompleteAdvance(long before, TimeSpan requested)
        {
            lock (_gate)
            {
                var after = _inner.GetTimestamp();
                var actual = _inner.GetElapsedTime(before, after);
                _advanceInProgress = false;
                if (_timerCallbackMovementViolation)
                {
                    _lastVerifiedTimestamp = after;
                    _hasExactTimingEvidence = false;
                    throw new TimingConfigurationException(
                        "The wrapped provider was moved from a timer callback during one scenario-controlled advance, " +
                        "so exact timing evidence is unavailable. Do not mutate the underlying provider from a timer " +
                        "callback.");
                }

                if (actual != requested)
                {
                    _lastVerifiedTimestamp = after;
                    _hasExactTimingEvidence = false;
                    throw new TimingConfigurationException(
                        $"The configured advance operation must move the wrapped provider by exactly the requested " +
                        $"duration. Requested {TimeFormat.Describe(requested)}, but the provider moved " +
                        $"{TimeFormat.Describe(actual)}. Ensure the delegate advances this provider once with the unchanged amount.");
                }

                _lastVerifiedTimestamp = after;
            }
        }

        internal void CancelAdvance(long before)
        {
            lock (_gate)
            {
                _advanceInProgress = false;
                var after = _inner.GetTimestamp();
                if (after != before || _timerCallbackMovementViolation)
                {
                    _lastVerifiedTimestamp = after;
                    _hasExactTimingEvidence = false;
                }
            }
        }

        internal long ObserveTimestamp()
        {
            lock (_gate)
            {
                var observed = _inner.GetTimestamp();
                if (!_advanceInProgress && observed != _lastVerifiedTimestamp)
                {
                    throw UnexpectedMovement(observed);
                }

                return observed;
            }
        }

        internal void ValidateStableState()
        {
            lock (_gate)
            {
                ValidateUnexpectedMovement();
            }
        }

        private void ValidateUnexpectedMovement()
        {
            if (_advanceInProgress)
            {
                return;
            }

            var observed = _inner.GetTimestamp();
            if (observed != _lastVerifiedTimestamp)
            {
                throw UnexpectedMovement(observed);
            }
        }

        internal void BeginTimerCallback()
        {
            if (_timerCallbackDepth.Value == 0)
            {
                _timerCallbackStartTimestamp.Value = _inner.GetTimestamp();
            }

            _timerCallbackDepth.Value++;
        }

        internal void EndTimerCallback()
        {
            var depth = _timerCallbackDepth.Value - 1;
            _timerCallbackDepth.Value = depth;
            if (depth != 0)
            {
                return;
            }

            var start = _timerCallbackStartTimestamp.Value;
            _timerCallbackStartTimestamp.Value = null;
            if (start is not { } callbackStart)
            {
                return;
            }

            var current = _inner.GetTimestamp();
            lock (_gate)
            {
                if (_advanceInProgress && current != callbackStart)
                {
                    _timerCallbackMovementViolation = true;
                }
            }
        }

        private TimingConfigurationException UnexpectedMovement(long observed)
        {
            var moved = _inner.GetElapsedTime(_lastVerifiedTimestamp, observed);
            _lastVerifiedTimestamp = observed;
            _hasExactTimingEvidence = false;
            return new TimingConfigurationException(
                $"The wrapped provider moved by {TimeFormat.Describe(moved)} outside the scenario-controlled " +
                "advance operation. Do not advance the underlying provider directly or share it with another clock.");
        }

    }

    private sealed class TrackingTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly ProviderController _controller;
        private readonly TimeProvider _inner;
        private readonly List<TrackedTimer> _timers = new();
        private long _timerCallbackVersion;
        private TaskCompletionSource<bool> _timerCallbackCompletion = NewTimerCallbackCompletion();
        private long _timerScheduleVersion;
        private TaskCompletionSource<bool> _timerScheduleCompletion = NewTimerScheduleCompletion();

        internal TrackingTimeProvider(TimeProvider inner, ProviderController controller)
        {
            _inner = inner;
            _controller = controller;
        }

        internal long TimerCallbackVersion => Interlocked.Read(ref _timerCallbackVersion);

        internal Task WaitForTimerCallbackAsync(long observedVersion)
        {
            lock (_gate)
            {
                if (_timerCallbackVersion != observedVersion)
                {
                    return Task.CompletedTask;
                }

                return _timerCallbackCompletion.Task;
            }
        }

        internal long TimerScheduleVersion => Interlocked.Read(ref _timerScheduleVersion);

        internal void BeginTimerCallback() => _controller.BeginTimerCallback();

        internal void EndTimerCallback() => _controller.EndTimerCallback();

        internal Task WaitForTimerScheduleAsync(long observedVersion)
        {
            lock (_gate)
            {
                if (_timerScheduleVersion != observedVersion)
                {
                    return Task.CompletedTask;
                }

                return _timerScheduleCompletion.Task;
            }
        }

        internal bool HasExactTimingEvidence => _controller.HasExactTimingEvidence;

        internal void BeginLogicalCall() => _controller.BeginLogicalCall();

        private static TaskCompletionSource<bool> NewTimerCallbackCompletion() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static TaskCompletionSource<bool> NewTimerScheduleCompletion() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private void SignalTimerSchedule()
        {
            TaskCompletionSource<bool> previous;
            lock (_gate)
            {
                previous = _timerScheduleCompletion;
                _timerScheduleCompletion = NewTimerScheduleCompletion();
                _timerScheduleVersion++;
            }

            previous.TrySetResult(true);
        }

        internal TimeSpan? NextTimerDue
        {
            get
            {
                lock (_gate)
                {
                    var now = _controller.ObserveTimestamp();
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
                if (timer.Period == Timeout.InfiniteTimeSpan || timer.Period == TimeSpan.Zero)
                {
                    timer.ClearScheduleAfterCallback();
                }
                else
                {
                    timer.SetScheduleAfterCallback(TimestampAfter(_controller.ObserveTimestamp(), timer.Period));
                }
            }

            SignalTimerSchedule();
        }

        internal void CompleteTimerCallback()
        {
            TaskCompletionSource<bool> previous;
            lock (_gate)
            {
                previous = _timerCallbackCompletion;
                _timerCallbackCompletion = NewTimerCallbackCompletion();
                _timerCallbackVersion++;
            }

            previous.TrySetResult(true);
        }

        internal TimerSchedule CaptureSchedule(TrackedTimer timer)
        {
            lock (_gate)
            {
                return timer.CaptureSchedule();
            }
        }

        internal long PublishSchedule(TrackedTimer timer, TimeSpan dueTime, TimeSpan period)
        {
            long version;
            lock (_gate)
            {
                timer.SetSchedule(dueTime, period);
                version = timer.ScheduleVersion;
            }

            SignalTimerSchedule();
            return version;
        }

        internal void RestoreSchedule(TrackedTimer timer, TimerSchedule previous, long publishedVersion)
        {
            var restored = false;
            lock (_gate)
            {
                if (timer.ScheduleVersion == publishedVersion && _timers.Contains(timer))
                {
                    timer.RestoreSchedule(previous);
                    restored = true;
                }
            }

            if (restored)
            {
                SignalTimerSchedule();
            }
        }

        public override DateTimeOffset GetUtcNow()
        {
            _controller.ValidateStableState();
            return _inner.GetUtcNow();
        }

        public override long GetTimestamp() => _controller.ObserveTimestamp();

        public override long TimestampFrequency
        {
            get
            {
                _controller.ValidateStableState();
                return _inner.TimestampFrequency;
            }
        }

        public override TimeZoneInfo LocalTimeZone
        {
            get
            {
                _controller.ValidateStableState();
                return _inner.LocalTimeZone;
            }
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            ArgumentNullException.ThrowIfNull(callback);
            _controller.ValidateStableState();

            var timer = new TrackedTimer(this, callback, state);
            timer.SetSchedule(dueTime, period);
            lock (_gate)
            {
                _timers.Add(timer);
            }

            SignalTimerSchedule();

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

        internal void RemoveTimer(TrackedTimer timer)
        {
            var removed = false;
            lock (_gate)
            {
                timer.MarkDisposed();
                removed = _timers.Remove(timer);
            }

            if (removed)
            {
                SignalTimerSchedule();
            }
        }

        internal long? TimestampAfterForTimer(TimeSpan duration) =>
            TimestampAfter(_controller.ObserveTimestamp(), duration);

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
            var previous = _owner.CaptureSchedule(this);
            var publishedVersion = _owner.PublishSchedule(this, dueTime, period);
            try
            {
                var changed = InnerTimer?.Change(dueTime, period) ?? false;
                if (!changed)
                {
                    _owner.RestoreSchedule(this, previous, publishedVersion);
                }

                return changed;
            }
            catch
            {
                _owner.RestoreSchedule(this, previous, publishedVersion);
                throw;
            }
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
            _owner.BeginTimerCallback();
            try
            {
                _owner.MarkTimerCallback(this);
                _callback(_state);
            }
            finally
            {
                _owner.CompleteTimerCallback();
                _owner.EndTimerCallback();
            }
        }

        internal void SetSchedule(TimeSpan dueTime, TimeSpan period)
        {
            Period = period;
            DueTimestamp = _owner.TimestampAfterForTimer(dueTime);
            ScheduleVersion++;
        }

        internal long ScheduleVersion { get; private set; }

        internal TimerSchedule CaptureSchedule() => new(DueTimestamp, Period, ScheduleVersion);

        internal void RestoreSchedule(TimerSchedule previous)
        {
            DueTimestamp = previous.DueTimestamp;
            Period = previous.Period;
            ScheduleVersion++;
        }

        internal void ClearScheduleAfterCallback()
        {
            DueTimestamp = null;
            ScheduleVersion++;
        }

        internal void SetScheduleAfterCallback(long? dueTimestamp)
        {
            DueTimestamp = dueTimestamp;
            ScheduleVersion++;
        }

        internal void MarkDisposed() => ScheduleVersion++;
    }

    internal readonly record struct TimerSchedule(long? DueTimestamp, TimeSpan Period, long Version);
}

internal sealed class TimingConfigurationException : InvalidOperationException
{
    internal TimingConfigurationException(string message)
        : base(message)
    {
    }
}
