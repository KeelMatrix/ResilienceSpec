using System.Net;

namespace KeelMatrix.ResilienceSpec;

internal static class LogicalCallOwnership
{
    internal static readonly HttpRequestOptionsKey<LogicalCallToken> RequestOption =
        new("KeelMatrix.ResilienceSpec.LogicalCall");

    internal static readonly AsyncLocal<LogicalCallToken?> Current = new();

    internal static bool IsOwnedBy(ScenarioObserver observer, HttpRequestMessage request)
    {
        var currentToken = Current.Value;
        if (currentToken is null ||
            !ReferenceEquals(currentToken.Owner, observer) ||
            !currentToken.IsActive)
        {
            return false;
        }

        return !request.Options.TryGetValue(RequestOption, out var requestToken) ||
            ReferenceEquals(requestToken, currentToken);
    }
}

internal sealed class LogicalCallToken
{
    private int _active = 1;

    internal LogicalCallToken(ScenarioObserver owner) => Owner = owner;

    internal ScenarioObserver Owner { get; }

    internal bool IsActive => Volatile.Read(ref _active) == 1;

    internal void Retire() => Volatile.Write(ref _active, 0);
}

internal enum AttemptPublicationPoint
{
    AfterCompletionPublication,
    BeforeSnapshotRead,
}

/// <summary>Provides a deterministic internal seam for publication-boundary tests.</summary>
internal sealed class AttemptPublicationSeam
{
    private readonly Action<AttemptPublicationPoint> _observe;

    internal AttemptPublicationSeam(Action<AttemptPublicationPoint> observe)
    {
        ArgumentNullException.ThrowIfNull(observe);
        _observe = observe;
    }

    internal void Observe(AttemptPublicationPoint point) => _observe(point);
}

/// <summary>Mutable state of one attempt while the scripted downstream is producing its outcome.</summary>
internal sealed class AttemptEntry
{
    private readonly object _gate = new();
    private readonly AttemptPublicationSeam? _publicationSeam;
    private Completion? _completion;
    private TimeSpan? _duration;
    private bool _durationIsExact;

    internal AttemptEntry(
        int ordinal,
        HttpMethod method,
        HttpFault? fault,
        TimeSpan? startedAfter,
        bool startedAfterIsExact,
        AttemptPublicationSeam? publicationSeam = null)
    {
        Ordinal = ordinal;
        Method = method;
        Fault = fault;
        StartedAfter = startedAfter;
        StartedAfterIsExact = startedAfterIsExact;
        _publicationSeam = publicationSeam;
    }

    internal int Ordinal { get; }

    internal HttpMethod Method { get; }

    internal HttpFault? Fault { get; }

    internal TimeSpan? StartedAfter { get; }

    internal bool StartedAfterIsExact { get; }

    internal void Complete(HttpAttemptOutcome outcome, HttpStatusCode? statusCode = null, TimeSpan? retryAfter = null)
    {
        // Publish all response metadata through one immutable reference. A live report can therefore observe either
        // the pre-completion Abandoned placeholder or the complete response record, never an outcome with missing
        // status or Retry-After metadata.
        Volatile.Write(ref _completion, new Completion(outcome, statusCode, retryAfter));
        _publicationSeam?.Observe(AttemptPublicationPoint.AfterCompletionPublication);
    }

    internal void Finish(TimeSpan? duration, bool durationIsExact)
    {
        lock (_gate)
        {
            _duration = duration;
            _durationIsExact = durationIsExact;
        }
    }

    internal HttpAttempt ToAttempt()
    {
        _publicationSeam?.Observe(AttemptPublicationPoint.BeforeSnapshotRead);
        var completion = Volatile.Read(ref _completion);
        TimeSpan? duration;
        bool durationIsExact;
        lock (_gate)
        {
            duration = _duration;
            durationIsExact = _durationIsExact;
        }

        return new HttpAttempt(
            Ordinal,
            Method,
            completion?.Outcome ?? HttpAttemptOutcome.Abandoned,
            completion?.StatusCode,
            completion?.RetryAfter,
            StartedAfter,
            duration)
        {
            StartedAfterIsExact = StartedAfterIsExact,
            DurationIsExact = durationIsExact,
        };
    }

    private sealed record Completion(
        HttpAttemptOutcome Outcome,
        HttpStatusCode? StatusCode,
        TimeSpan? RetryAfter);
}

/// <summary>Scopes one attempt so that the timeline is finalized exactly once.</summary>
internal sealed class AttemptScope : IDisposable
{
    private readonly ScenarioObserver _observer;
    private bool _disposed;

    internal AttemptScope(ScenarioObserver observer, AttemptEntry entry)
    {
        _observer = observer;
        Entry = entry;
    }

    internal AttemptEntry Entry { get; }

    internal void Complete(HttpAttemptOutcome outcome, HttpStatusCode? statusCode = null, TimeSpan? retryAfter = null) =>
        Entry.Complete(outcome, statusCode, retryAfter);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _observer.EndAttempt(Entry);
    }
}

/// <summary>Holds a script's single-consumer lease for one complete logical client call.</summary>
internal sealed class LogicalCallScope : IDisposable
{
    private readonly ScenarioObserver _observer;
    private readonly LogicalCallToken _token;
    private bool _disposed;

    internal LogicalCallScope(ScenarioObserver observer)
    {
        _observer = observer;
        _token = new LogicalCallToken(observer);
    }

    internal IDisposable Enter(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Options.Set(LogicalCallOwnership.RequestOption, _token);
        var previous = LogicalCallOwnership.Current.Value;
        LogicalCallOwnership.Current.Value = _token;
        return new ExecutionContextScope(previous);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _token.Retire();
        _observer.EndLogicalCall();
    }

    private sealed class ExecutionContextScope : IDisposable
    {
        private readonly LogicalCallToken? _previous;
        private bool _disposed;

        internal ExecutionContextScope(LogicalCallToken? previous) => _previous = previous;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            LogicalCallOwnership.Current.Value = _previous;
        }
    }
}

/// <summary>
/// Owns the bounded attempt timeline of one scripted downstream: attempt numbering, injected-clock timing,
/// single-consumer enforcement, and the activation signal.
/// </summary>
internal sealed class ScenarioObserver
{
    private readonly object _gate = new();
    private readonly List<AttemptEntry> _entries = new();
    private readonly HttpFaultScript _script;
    private readonly TimeProvider? _clock;
    private readonly ResilienceScenarioOptions _options;
    private readonly long _startTimestamp;
    private int _inFlight;
    private int _ordinal;
    private bool _overflowed;
    private bool _logicalCallConsumed;
    private bool _observationCleanupActive;
    private bool _observationCutoff;
    private long _progressVersion;
    private TaskCompletionSource<bool> _progress = NewProgressSource();
    private bool _settled;
    private TimeSpan? _settledVirtualElapsed;
    private bool _settledVirtualElapsedIsExact;

    internal ScenarioObserver(HttpFaultScript script, TimeProvider? clock, ResilienceScenarioOptions options)
    {
        _script = script;
        _clock = clock;
        _options = options;
        _startTimestamp = clock?.GetTimestamp() ?? 0;
        Telemetry = new ScenarioTelemetry(options.TelemetrySink, script, clock is not null);
    }

    internal ScenarioTelemetry Telemetry { get; }

    internal LogicalCallScope BeginLogicalCall()
    {
        lock (_gate)
        {
            if (_logicalCallConsumed)
            {
                throw new ScenarioConsumedException(
                    "This ResilienceScenario has already served one logical request and cannot be reused. " +
                    "Create one scenario per logical call.");
            }

            _logicalCallConsumed = true;
        }

        return new LogicalCallScope(this);
    }

    internal int StepCount => _script.StepCount;

    /// <summary>
    /// Gets the exact number of attempts that reached this downstream, including attempts that could not be kept in
    /// the bounded recorded timeline.
    /// </summary>
    internal int AttemptCount
    {
        get
        {
            lock (_gate)
            {
                return _ordinal;
            }
        }
    }

    internal AttemptScope BeginAttempt(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!LogicalCallOwnership.IsOwnedBy(this, request))
        {
            throw new ScenarioConsumedException(
                "This request did not start through ResilienceScenario.SendAsync, so it cannot reach the scripted " +
                "downstream. Run one logical operation through the scenario and create one scenario per logical call.");
        }

        AttemptEntry entry;
        lock (_gate)
        {
            if (_inFlight > 0)
            {
                throw new ConcurrentScriptUseException(
                    "The scripted downstream is already serving one in-flight request, so a second request cannot consume the script deterministically. " +
                    "Create one scenario per logical call.");
            }

            _inFlight++;
            _settled = false;
            _settledVirtualElapsed = null;
            _settledVirtualElapsedIsExact = false;
            var ordinal = ++_ordinal;
            entry = new AttemptEntry(
                ordinal,
                request.Method,
                _script.StepAt(ordinal),
                Elapsed(),
                HasExactTimingEvidence());
            if (_entries.Count < HttpAttemptReport.MaximumRecordedAttempts)
            {
                _entries.Add(entry);
            }
            else
            {
                // The timeline is bounded. The attempt is still served so that the client under test keeps seeing
                // the assembled behaviour it was configured for, but the report has to declare the incomplete
                // timeline instead of presenting a truncated one as if it were complete.
                _overflowed = true;
            }
        }

        SignalProgress();

        return new AttemptScope(this, entry);
    }

    internal void EndAttempt(AttemptEntry entry)
    {
        bool failed;
        lock (_gate)
        {
            var finished = Elapsed();
            var durationIsExact = HasExactTimingEvidence();
            entry.Finish(
                !_observationCleanupActive && finished is { } end && entry.StartedAfter is { } start
                    ? end - start
                    : null,
                !_observationCleanupActive && entry.StartedAfterIsExact && durationIsExact);
            _inFlight--;
            failed = entry.Fault?.IsFailure == true;
        }

        SignalProgress();

        if (failed)
        {
            Telemetry.RecordFailure();
        }
    }

    internal void MarkObservationCleanupStarted()
    {
        lock (_gate)
        {
            _observationCleanupActive = true;
        }
    }

    internal TimeSpan? MarkSettled()
    {
        var virtualElapsed = Elapsed();
        var virtualElapsedIsExact = HasExactTimingEvidence();
        lock (_gate)
        {
            _settled = true;
            _observationCutoff = false;
            _settledVirtualElapsed = virtualElapsed;
            _settledVirtualElapsedIsExact = virtualElapsedIsExact;
        }

        SignalProgress();
        return virtualElapsed;
    }

    internal void MarkObservationCutoff()
    {
        lock (_gate)
        {
            _settled = false;
            _observationCutoff = true;
            _settledVirtualElapsed = null;
            _settledVirtualElapsedIsExact = false;
        }

        SignalProgress();
    }

    internal long ProgressVersion
    {
        get
        {
            lock (_gate)
            {
                return _progressVersion;
            }
        }
    }

    internal async Task<bool> WaitForProgressAsync(long observedVersion, TimeSpan timeout)
    {
        Task progress;
        lock (_gate)
        {
            if (_progressVersion != observedVersion)
            {
                return true;
            }

            progress = _progress.Task;
        }

        var completed = await Task.WhenAny(progress, Task.Delay(timeout)).ConfigureAwait(false);
        if (ReferenceEquals(completed, progress))
        {
            return true;
        }

        // A progress signal can race the watchdog at the scheduling boundary. Re-read the version after the delay
        // wins so a signal already published by the time this method returns is not misclassified as a stalled
        // pipeline under parallel test load.
        lock (_gate)
        {
            return _progressVersion != observedVersion;
        }
    }

    internal void EndLogicalCall()
    {
        lock (_gate)
        {
            // The call lease releases retained cleanup resources, but consumption is permanent for the scenario.
            _logicalCallConsumed = true;
        }
    }

    internal HttpAttemptReport Snapshot()
    {
        lock (_gate)
        {
            var attempts = new HttpAttempt[_entries.Count];
            for (var index = 0; index < _entries.Count; index++)
            {
                attempts[index] = _entries[index].ToAttempt();
            }

            return new HttpAttemptReport(
                attempts,
                _ordinal,
                _overflowed,
                _settled,
                _observationCutoff,
                _settledVirtualElapsed,
                _clock is null ? null : _options.AdvanceStep,
                _settledVirtualElapsedIsExact,
                Telemetry);
        }
    }

    private TimeSpan? Elapsed() => _clock?.GetElapsedTime(_startTimestamp);

    private bool HasExactTimingEvidence() =>
        _clock is not null && ResilienceScenarioClock.HasExactTimingEvidence(_clock);

    private void SignalProgress()
    {
        TaskCompletionSource<bool> previous;
        lock (_gate)
        {
            _progressVersion++;
            previous = _progress;
            _progress = NewProgressSource();
        }

        previous.TrySetResult(true);
    }

    private static TaskCompletionSource<bool> NewProgressSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
