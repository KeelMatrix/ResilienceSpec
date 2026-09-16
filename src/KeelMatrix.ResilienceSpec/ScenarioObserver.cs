using System.Net;

namespace KeelMatrix.ResilienceSpec;

/// <summary>Mutable state of one attempt while the scripted downstream is producing its outcome.</summary>
internal sealed class AttemptEntry
{
    private HttpAttemptOutcome? _outcome;
    private HttpStatusCode? _statusCode;
    private TimeSpan? _retryAfter;
    private TimeSpan? _duration;

    internal AttemptEntry(int ordinal, HttpMethod method, HttpFault? fault, TimeSpan? startedAfter)
    {
        Ordinal = ordinal;
        Method = method;
        Fault = fault;
        StartedAfter = startedAfter;
    }

    internal int Ordinal { get; }

    internal HttpMethod Method { get; }

    internal HttpFault? Fault { get; }

    internal TimeSpan? StartedAfter { get; }

    internal void Complete(HttpAttemptOutcome outcome, HttpStatusCode? statusCode = null, TimeSpan? retryAfter = null)
    {
        _outcome = outcome;
        _statusCode = statusCode;
        _retryAfter = retryAfter;
    }

    internal void Finish(TimeSpan? duration) => _duration = duration;

    internal HttpAttempt ToAttempt() => new(
        Ordinal,
        Method,
        _outcome ?? HttpAttemptOutcome.Abandoned,
        _statusCode,
        _retryAfter,
        StartedAfter,
        _duration);
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
    private bool _settled;
    private TimeSpan? _settledVirtualElapsed;

    internal ScenarioObserver(HttpFaultScript script, TimeProvider? clock, ResilienceScenarioOptions options)
    {
        _script = script;
        _clock = clock;
        _options = options;
        _startTimestamp = clock?.GetTimestamp() ?? 0;
        Telemetry = new ScenarioTelemetry(options.TelemetrySink, script, clock is not null);
    }

    internal ScenarioTelemetry Telemetry { get; }

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

    internal AttemptScope BeginAttempt(HttpMethod method)
    {
        AttemptEntry entry;
        lock (_gate)
        {
            if (_options.Concurrency == ScriptConcurrency.SingleConsumer && _inFlight > 0)
            {
                throw new ConcurrentScriptUseException(
                    "The scripted downstream is already serving one in-flight request, so a second request cannot consume the script deterministically. " +
                    "Create one scenario per logical call, or use ScriptConcurrency.AllowConcurrent when the scenario under test is genuinely concurrent.");
            }

            _inFlight++;
            _settled = false;
            _settledVirtualElapsed = null;
            var ordinal = ++_ordinal;
            entry = new AttemptEntry(ordinal, method, _script.StepAt(ordinal), Elapsed());
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

        return new AttemptScope(this, entry);
    }

    internal void EndAttempt(AttemptEntry entry)
    {
        bool failed;
        lock (_gate)
        {
            var finished = Elapsed();
            entry.Finish(
                finished is { } end && entry.StartedAfter is { } start
                    ? end - start
                    : null);
            _inFlight--;
            failed = entry.Fault?.IsFailure == true;
        }

        if (failed)
        {
            Telemetry.RecordFailure();
        }
    }

    internal void MarkSettled(TimeSpan? virtualElapsed)
    {
        lock (_gate)
        {
            _settled = true;
            _settledVirtualElapsed = virtualElapsed;
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
                _settledVirtualElapsed,
                _clock is null ? null : _options.AdvanceStep,
                Telemetry);
        }
    }

    private TimeSpan? Elapsed() => _clock?.GetElapsedTime(_startTimestamp);
}
