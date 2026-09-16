using System.Diagnostics;
using System.Net.Http.Headers;

namespace ResilienceSpec.Probe;

/// <summary>What the scripted downstream was asked to do for one attempt.</summary>
public sealed record AttemptRecord(int Ordinal, string Observer, string Method, string ScriptedOutcome, TimeSpan SinceStart, bool BeyondScript);

/// <summary>
/// In-memory terminal handler. It is the innermost handler of the probe chains, so every attempt the
/// application handler chain produces is observable here without any network interaction.
/// </summary>
public sealed class ScriptedTerminalHandler : HttpMessageHandler
{
    /// <summary>The default observer name used in timelines and attempt records.</summary>
    public const string ObserverName = "terminal";

    private readonly ScriptStep[] _steps;
    private readonly AttemptTimeline _timeline;
    private readonly string _observer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<AttemptRecord> _records = new();
    private readonly object _gate = new();
    private int _attempts;

    public ScriptedTerminalHandler(IEnumerable<ScriptStep> steps, AttemptTimeline timeline, string observer = ObserverName)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentException.ThrowIfNullOrWhiteSpace(observer);

        _steps = steps.ToArray();
        _timeline = timeline;
        _observer = observer;
    }

    /// <summary>Number of attempts that reached this handler.</summary>
    public int AttemptCount => Volatile.Read(ref _attempts);

    /// <summary>Attempt details in arrival order.</summary>
    public IReadOnlyList<AttemptRecord> Records
    {
        get
        {
            lock (_gate)
            {
                return _records.ToArray();
            }
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var ordinal = Interlocked.Increment(ref _attempts);
        var step = ordinal <= _steps.Length ? _steps[ordinal - 1] : null;
        var beyondScript = step is null;
        var outcome = beyondScript ? "script-exhausted" : step!.Describe();

        lock (_gate)
        {
            _records.Add(new AttemptRecord(ordinal, _observer, request.Method.Method, outcome, _clock.Elapsed, beyondScript));
        }

        _timeline.Record(_observer, request, outcome);

        if (beyondScript)
        {
            throw new InvalidOperationException("The scripted downstream was called more times than the script provides steps.");
        }

        switch (step!.Kind)
        {
            case ScriptStepKind.NetworkError:
                throw new HttpRequestException("scripted downstream failure");

            case ScriptStepKind.Hang:
                await HangUntilCanceledAsync(cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException("A hanging attempt completes only through cancellation.");

            default:
                return CreateResponse(step, request);
        }
    }

    private static HttpResponseMessage CreateResponse(ScriptStep step, HttpRequestMessage request)
    {
        var response = new HttpResponseMessage(step.StatusCode) { RequestMessage = request };
        if (step.RetryAfterDelta is { } delta)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(delta);
        }
        else if (step.RetryAfterDate is { } date)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(date);
        }

        return response;
    }

    private static async Task HangUntilCanceledAsync(CancellationToken cancellationToken)
    {
        // No finite wait is involved: the attempt ends only when the caller or a timeout strategy cancels it.
        var neverCompletes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), neverCompletes))
        {
            await neverCompletes.Task.ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }
}
