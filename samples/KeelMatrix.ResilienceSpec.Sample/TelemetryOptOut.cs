namespace KeelMatrix.ResilienceSpec.Sample;

/// <summary>
/// Sample runs are development activity, so they never emit production telemetry.
/// </summary>
internal static class TelemetryOptOut
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Apply() =>
        Environment.SetEnvironmentVariable("KEELMATRIX_NO_TELEMETRY", "1");
}
