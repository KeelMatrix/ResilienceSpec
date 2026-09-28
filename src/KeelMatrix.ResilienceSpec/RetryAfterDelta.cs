using System.Net.Http.Headers;

namespace KeelMatrix.ResilienceSpec;

/// <summary>Represents the integer delta-seconds form carried by an HTTP <c>Retry-After</c> header.</summary>
internal readonly struct RetryAfterDelta
{
    private RetryAfterDelta(int seconds)
    {
        Seconds = seconds;
    }

    internal int Seconds { get; }

    internal TimeSpan Duration => TimeSpan.FromSeconds(Seconds);

    internal static RetryAfterDelta FromDuration(string parameterName, TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "A Retry-After delta must not be negative.");
        }

        if (value.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new ArgumentException(
                "A Retry-After delta must use whole-second precision because the HTTP wire form carries integer " +
                "delta-seconds. Fractional and sub-second values are not representable.",
                parameterName);
        }

        var seconds = value.Ticks / TimeSpan.TicksPerSecond;
        if (seconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"A Retry-After delta exceeds the HTTP delta-seconds wire limit ({int.MaxValue} seconds).");
        }

        return new RetryAfterDelta((int)seconds);
    }

    internal RetryConditionHeaderValue ToHeaderValue() => new(Duration);
}
