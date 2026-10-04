using KeelMatrix.Telemetry;

namespace KeelMatrix.ResilienceSpec;

/// <summary>The no-payload seam to the shared telemetry client.</summary>
internal interface ITelemetrySink
{
    void TrackActivation();

    void TrackHeartbeat();
}

/// <summary>Forwards no-argument signal requests to the shared client.</summary>
internal sealed class SharedTelemetrySink : ITelemetrySink
{
    private SharedTelemetrySink()
    {
    }

    internal static SharedTelemetrySink Instance { get; } = new();

    public void TrackActivation() => TelemetryHost.TrackActivation();

    public void TrackHeartbeat() => TelemetryHost.TrackHeartbeat();
}

/// <summary>The sole adapter to KeelMatrix.Telemetry.</summary>
internal static class TelemetryHost
{
    internal const string ToolName = "ResilienceSpec";

    private static readonly Client Instance = new(ToolName, typeof(TelemetryHost));

    internal static void TrackActivation() => Instance.TrackActivation();

    internal static void TrackHeartbeat() => Instance.TrackHeartbeat();
}

/// <summary>Applies ResilienceSpec's activation eligibility to settled scenarios.</summary>
internal sealed class ScenarioTelemetry
{
    private readonly ITelemetrySink _sink;
    private readonly object _gate = new();
    private bool _failureObserved;
    private bool _scenarioCompleted;

    internal ScenarioTelemetry(ITelemetrySink sink)
    {
        _sink = sink;
    }

    internal void RecordFailure()
    {
        lock (_gate)
        {
            _failureObserved = true;
        }
    }

    internal void MarkScenarioCompleted()
    {
        lock (_gate)
        {
            _scenarioCompleted = true;
        }
    }

    internal void RecordAssertionEvaluation()
    {
        bool eligible;
        lock (_gate)
        {
            eligible = _scenarioCompleted && _failureObserved;
        }

        if (!eligible)
        {
            return;
        }

        _sink.TrackActivation();
        _sink.TrackHeartbeat();
    }
}
