using System.Globalization;

namespace KeelMatrix.ResilienceSpec;

/// <summary>Renders durations in the compact invariant form used by timelines and diagnostics.</summary>
internal static class TimeFormat
{
    /// <summary>Renders one duration without depending on the current culture.</summary>
    internal static string Describe(TimeSpan value)
    {
        if (value == TimeSpan.Zero)
        {
            return "0 ms";
        }

        if (Math.Abs(value.TotalMilliseconds) < 1000)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{value.TotalMilliseconds:0.###} ms");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value.TotalSeconds:0.###} s");
    }
}
