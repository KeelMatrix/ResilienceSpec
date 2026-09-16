using System.Globalization;
using KeelMatrix.Telemetry;

namespace KeelMatrix.ResilienceSpec;

/// <summary>Buckets an observed attempt count so telemetry never carries an unbounded number.</summary>
internal enum AttemptCountBucket
{
    /// <summary>No attempt was recorded.</summary>
    None,

    /// <summary>Exactly one attempt was recorded.</summary>
    Single,

    /// <summary>Two or three attempts were recorded.</summary>
    TwoToThree,

    /// <summary>Four to ten attempts were recorded.</summary>
    FourToTen,

    /// <summary>More than ten attempts were recorded.</summary>
    MoreThanTen,
}

/// <summary>Coarse result of the evaluated assertion.</summary>
internal enum AssertionOutcome
{
    /// <summary>The assertion held.</summary>
    Passed,

    /// <summary>The assertion did not hold.</summary>
    Failed,
}

/// <summary>Coarse integration path that produced the observation.</summary>
internal enum IntegrationPath
{
    /// <summary>The scripted handler was used directly.</summary>
    ScriptedDownstream,

    /// <summary>The scripted handler was installed through the HttpClientFactory adapter.</summary>
    HttpClientFactory,
}

/// <summary>
/// The complete set of facts a scenario may report about an activation. Every field is coarse and enumerable:
/// there is deliberately no place for a client name, URI, host, path, query, header, body, cookie, authorization
/// value, exception message, raw HTTP method, fault-script content, repository name, or local path.
/// </summary>
/// <param name="PackageVersion">The package version that produced the activation.</param>
/// <param name="TargetFramework">The target framework of the package.</param>
/// <param name="ResponseFault">Whether the script contained a failing response step.</param>
/// <param name="ExceptionFault">Whether the script contained a failing exception step.</param>
/// <param name="TimingAssertion">Whether a controllable clock took part in the scenario.</param>
/// <param name="AttemptBucket">The coarse attempt-count bucket.</param>
/// <param name="Assertion">Whether the evaluated assertion held.</param>
/// <param name="Integration">The coarse integration path.</param>
internal sealed record ScenarioTelemetrySignal(
    string PackageVersion,
    string TargetFramework,
    bool ResponseFault,
    bool ExceptionFault,
    bool TimingAssertion,
    AttemptCountBucket AttemptBucket,
    AssertionOutcome Assertion,
    IntegrationPath Integration)
{
    internal string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"package-version={PackageVersion} tfm={TargetFramework} response-fault={ResponseFault} exception-fault={ExceptionFault} timing-assertion={TimingAssertion} attempts={AttemptBucket} assertion={Assertion} integration={Integration}");
}

/// <summary>Receives the activation decision of one scenario.</summary>
internal interface ITelemetrySink
{
    void TrackActivation(ScenarioTelemetrySignal signal);
}

/// <summary>Forwards activation to the shared KeelMatrix telemetry client.</summary>
internal sealed class SharedTelemetrySink : ITelemetrySink
{
    private SharedTelemetrySink()
    {
    }

    internal static SharedTelemetrySink Instance { get; } = new();

    public void TrackActivation(ScenarioTelemetrySignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        TelemetryHost.TrackScenarioActivation();
    }
}

/// <summary>
/// The only place in the package that talks to <c>KeelMatrix.Telemetry</c>.
/// </summary>
/// <remarks>
/// Telemetry is best effort and never a reliability dependency: the shared client is constructed once, is a no-op
/// when telemetry is disabled through the documented opt-out, and never throws or blocks the caller.
/// </remarks>
internal static class TelemetryHost
{
    internal const string ToolName = "ResilienceSpec";

    private static readonly Client Instance = new(ToolName, typeof(TelemetryHost));

    internal static void TrackScenarioActivation()
    {
        Instance.TrackActivation();
        Instance.TrackHeartbeat();
    }
}

/// <summary>
/// Decides whether one scenario counts as an activation: the scripted downstream must have produced at least one
/// injected failure and at least one resilience assertion must have been evaluated. Constructing a handler, a
/// script, or a scenario is never an activation on its own.
/// </summary>
internal sealed class ScenarioTelemetry
{
    internal const string SupportedTargetFramework = "net8.0";

    private readonly ITelemetrySink _sink;
    private readonly HttpFaultScript _script;
    private readonly bool _timingAssertionsAvailable;
    private readonly object _gate = new();
    private bool _failureObserved;
    private bool _activationRequested;
    private IntegrationPath _integration = IntegrationPath.ScriptedDownstream;

    internal ScenarioTelemetry(ITelemetrySink sink, HttpFaultScript script, bool timingAssertionsAvailable)
    {
        _sink = sink;
        _script = script;
        _timingAssertionsAvailable = timingAssertionsAvailable;
    }

    internal void RecordFailure()
    {
        lock (_gate)
        {
            _failureObserved = true;
        }
    }

    internal void MarkIntegrationPath(IntegrationPath integration)
    {
        lock (_gate)
        {
            _integration = integration;
        }
    }

    internal void RecordAssertionEvaluation(bool passed, int attemptCount)
    {
        ScenarioTelemetrySignal? signal = null;
        lock (_gate)
        {
            if (!_failureObserved || _activationRequested)
            {
                return;
            }

            _activationRequested = true;
            signal = new ScenarioTelemetrySignal(
                typeof(ScenarioTelemetry).Assembly.GetName().Version?.ToString() ?? "unknown",
                SupportedTargetFramework,
                _script.ContainsResponseFault,
                _script.ContainsExceptionFault,
                _timingAssertionsAvailable,
                Bucket(attemptCount),
                passed ? AssertionOutcome.Passed : AssertionOutcome.Failed,
                _integration);
        }

        _sink.TrackActivation(signal);
    }

    private static AttemptCountBucket Bucket(int attemptCount) => attemptCount switch
    {
        <= 0 => AttemptCountBucket.None,
        1 => AttemptCountBucket.Single,
        <= 3 => AttemptCountBucket.TwoToThree,
        <= 10 => AttemptCountBucket.FourToTen,
        _ => AttemptCountBucket.MoreThanTen,
    };
}
