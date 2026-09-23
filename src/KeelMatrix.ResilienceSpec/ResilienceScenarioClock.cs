using System.Reflection;
using System.Runtime.Loader;

namespace KeelMatrix.ResilienceSpec;

/// <summary>
/// Wraps the supported <c>Microsoft.Extensions.Time.Testing.FakeTimeProvider</c>, verifies scenario-controlled time
/// movement, and records timers that fire during a virtual-time advance.
/// </summary>
/// <remarks>
/// <para>
/// Use this clock both in the client under test and in <see cref="ResilienceScenario"/>. The wrapped provider remains
/// responsible for virtual time; the wrapper adds the progress signal needed to distinguish an ordinary intermediate
/// delay from a timer that fired but whose continuation has not reached the scripted downstream yet.
/// </para>
/// <para>
/// The wrapped provider must keep <c>AutoAdvanceAmount</c> at zero, and its advance operation must move that provider
/// by exactly the requested duration and synchronously dispatch timers released by the advance. Movement outside that
/// operation is rejected. The package admits only version 10.10.0.0 and the runtime <see cref="Type"/> identity loaded
/// from the <c>Microsoft.Extensions.TimeProvider.Testing.dll</c> file beside the package assembly, after checking the
/// expected Microsoft strong-name public-key token. Derived, delegating, and name-spoofed consumer types are rejected
/// because their assembly provenance is not that resolved dependency path; the check does not attest a file that a
/// consumer replaces at that exact path.
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
    /// <exception cref="ArgumentException"><paramref name="inner"/> is <see cref="TimeProvider.System"/>, is not the exact runtime type loaded from the supported <c>Microsoft.Extensions.TimeProvider.Testing</c> 10.10.0 assembly, has automatic advancement enabled, or is already tracking another clock.</exception>
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
                "Timing scenarios require the exact runtime identity of Microsoft.Extensions.Time.Testing.FakeTimeProvider " +
                "from Microsoft.Extensions.TimeProvider.Testing 10.10.0. " +
                "Consumer-authored derived, delegating, or name-spoofed TimeProvider types cannot prove deterministic timing.",
                nameof(inner));
        }

        var autoAdvanceAmount = GetAutoAdvanceAmount(inner);
        if (autoAdvanceAmount != TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"The supported FakeTimeProvider must have AutoAdvanceAmount set to zero, but it is " +
                $"{TimeFormat.Describe(autoAdvanceAmount)}. Timing evidence is valid only when the scenario-controlled " +
                "advance operation is the sole source of virtual-time movement.",
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

    private const string SupportedControllableProviderTypeName = "Microsoft.Extensions.Time.Testing.FakeTimeProvider";
    private const string SupportedControllableProviderAssemblyName = "Microsoft.Extensions.TimeProvider.Testing";
    private const string SupportedControllableProviderAssemblyFileName = "Microsoft.Extensions.TimeProvider.Testing.dll";
    private const string AutoAdvanceAmountPropertyName = "AutoAdvanceAmount";
    private static readonly byte[] SupportedControllableProviderPublicKeyToken =
        Convert.FromHexString("31BF3856AD364E35");
    private static readonly Version SupportedControllableProviderAssemblyVersion = new(10, 10, 0, 0);

    private static readonly Type? SupportedControllableProviderType = ResolveSupportedControllableProviderType();
    private static readonly PropertyInfo? AutoAdvanceAmountProperty = SupportedControllableProviderType?.GetProperty(
        AutoAdvanceAmountPropertyName,
        BindingFlags.Instance | BindingFlags.Public);

    private static bool IsSupportedControllableProvider(TimeProvider provider) =>
        SupportedControllableProviderType is { } supportedType && provider.GetType() == supportedType;

    private static Type? ResolveSupportedControllableProviderType()
    {
        try
        {
            var packageAssemblyPath = typeof(ResilienceScenarioClock).Assembly.Location;
            if (string.IsNullOrWhiteSpace(packageAssemblyPath))
            {
                return null;
            }

            var packageDirectory = Path.GetDirectoryName(packageAssemblyPath);
            if (string.IsNullOrWhiteSpace(packageDirectory))
            {
                return null;
            }

            var trustedAssemblyPath = Path.Combine(packageDirectory, SupportedControllableProviderAssemblyFileName);
            if (!File.Exists(trustedAssemblyPath))
            {
                return null;
            }

            var fileAssemblyName = AssemblyName.GetAssemblyName(trustedAssemblyPath);
            if (!HasSupportedAssemblyIdentity(fileAssemblyName))
            {
                return null;
            }

            var loadContext = AssemblyLoadContext.GetLoadContext(typeof(ResilienceScenarioClock).Assembly);
            if (loadContext is null)
            {
                return null;
            }

            var trustedAssembly = loadContext.LoadFromAssemblyPath(trustedAssemblyPath);
            if (!PathsEqual(trustedAssembly.Location, trustedAssemblyPath) ||
                !HasSupportedAssemblyIdentity(trustedAssembly.GetName()))
            {
                return null;
            }

            return trustedAssembly.GetType(
                SupportedControllableProviderTypeName,
                throwOnError: false,
                ignoreCase: false);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (BadImageFormatException)
        {
            return null;
        }
        catch (FileLoadException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool HasSupportedAssemblyIdentity(AssemblyName assemblyName) =>
        string.Equals(assemblyName.Name, SupportedControllableProviderAssemblyName, StringComparison.Ordinal) &&
        assemblyName.Version == SupportedControllableProviderAssemblyVersion &&
        assemblyName.GetPublicKeyToken() is { } token &&
        token.AsSpan().SequenceEqual(SupportedControllableProviderPublicKeyToken);

    private static bool PathsEqual(string left, string right) =>
        !string.IsNullOrWhiteSpace(left) &&
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    internal static long GetTimerCallbackVersion(TimeProvider provider) =>
        ((TrackingTimeProvider)provider).TimerCallbackVersion;

    internal static TimeSpan? GetNextTimerDue(TimeProvider provider) =>
        ((TrackingTimeProvider)provider).NextTimerDue;

    internal static bool HasExactTimingEvidence(TimeProvider provider) =>
        ((TrackingTimeProvider)provider).HasExactTimingEvidence;

    private static TimeSpan GetAutoAdvanceAmount(TimeProvider provider)
    {
        if (AutoAdvanceAmountProperty?.PropertyType != typeof(TimeSpan) ||
            AutoAdvanceAmountProperty.GetValue(provider) is not TimeSpan amount)
        {
            throw new ArgumentException(
                "The admitted FakeTimeProvider does not expose the expected TimeSpan AutoAdvanceAmount contract.",
                nameof(provider));
        }

        return amount;
    }

    private sealed class ProviderController
    {
        private readonly object _gate = new();
        private readonly TimeProvider _inner;
        private long _lastVerifiedTimestamp;
        private bool _advanceInProgress;
        private bool _hasExactTimingEvidence = true;

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
                    ValidateAutoAdvanceDisabled();
                    ValidateUnexpectedMovement();
                    return _hasExactTimingEvidence;
                }
            }
        }

        internal long BeginAdvance(bool producesExactEvidence)
        {
            lock (_gate)
            {
                ValidateAutoAdvanceDisabled();
                ValidateUnexpectedMovement();
                if (_advanceInProgress)
                {
                    throw new TimingConfigurationException(
                        "The scenario attempted to advance its FakeTimeProvider recursively. " +
                        "Use one scenario-controlled advance operation per virtual-time step.");
                }

                _advanceInProgress = true;
                _hasExactTimingEvidence &= producesExactEvidence;
                return _lastVerifiedTimestamp;
            }
        }

        internal void CompleteAdvance(long before, TimeSpan requested)
        {
            lock (_gate)
            {
                ValidateAutoAdvanceDisabled();
                var after = _inner.GetTimestamp();
                _advanceInProgress = false;
                var actual = _inner.GetElapsedTime(before, after);
                if (actual != requested)
                {
                    _lastVerifiedTimestamp = after;
                    _hasExactTimingEvidence = false;
                    throw new TimingConfigurationException(
                        $"The configured advance operation must move the wrapped FakeTimeProvider by exactly the requested " +
                        $"duration. Requested {TimeFormat.Describe(requested)}, but the admitted provider moved " +
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
                if (GetAutoAdvanceAmount(_inner) == TimeSpan.Zero)
                {
                    var after = _inner.GetTimestamp();
                    if (after != before)
                    {
                        _lastVerifiedTimestamp = after;
                        _hasExactTimingEvidence = false;
                    }
                }
            }
        }

        internal long ObserveTimestamp()
        {
            lock (_gate)
            {
                ValidateAutoAdvanceDisabled();
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
                ValidateAutoAdvanceDisabled();
                ValidateUnexpectedMovement();
            }
        }

        private void ValidateAutoAdvanceDisabled()
        {
            var amount = GetAutoAdvanceAmount(_inner);
            if (amount != TimeSpan.Zero)
            {
                throw new TimingConfigurationException(
                    $"FakeTimeProvider.AutoAdvanceAmount changed to {TimeFormat.Describe(amount)}. It must remain zero " +
                    "for the complete scenario run so observing the clock cannot move virtual time.");
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

        private TimingConfigurationException UnexpectedMovement(long observed)
        {
            var moved = _inner.GetElapsedTime(_lastVerifiedTimestamp, observed);
            _lastVerifiedTimestamp = observed;
            _hasExactTimingEvidence = false;
            return new TimingConfigurationException(
                $"The admitted FakeTimeProvider moved by {TimeFormat.Describe(moved)} outside the scenario-controlled " +
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

        internal TrackingTimeProvider(TimeProvider inner, ProviderController controller)
        {
            _inner = inner;
            _controller = controller;
        }

        internal long TimerCallbackVersion => Interlocked.Read(ref _timerCallbackVersion);

        internal bool HasExactTimingEvidence => _controller.HasExactTimingEvidence;

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
                if (timer.Period == Timeout.InfiniteTimeSpan)
                {
                    timer.DueTimestamp = null;
                }
                else
                {
                    timer.DueTimestamp = TimestampAfter(_controller.ObserveTimestamp(), timer.Period);
                }

                _timerCallbackVersion++;
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

internal sealed class TimingConfigurationException : InvalidOperationException
{
    internal TimingConfigurationException(string message)
        : base(message)
    {
    }
}
