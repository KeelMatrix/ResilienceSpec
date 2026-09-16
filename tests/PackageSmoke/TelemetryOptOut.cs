namespace PackageSmoke;

/// <summary>
/// The package-consumer smoke is repository validation, so it never emits production telemetry.
/// </summary>
internal static class TelemetryOptOut
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Apply() =>
        Environment.SetEnvironmentVariable("KEELMATRIX_NO_TELEMETRY", "1");
}
