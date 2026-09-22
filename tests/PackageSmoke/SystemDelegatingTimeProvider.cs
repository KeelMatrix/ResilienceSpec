namespace PackageSmoke;

public sealed class SystemDelegatingTimeProvider : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow();

    public override long GetTimestamp() => TimeProvider.System.GetTimestamp();

    public override long TimestampFrequency => TimeProvider.System.TimestampFrequency;

    public override TimeZoneInfo LocalTimeZone => TimeProvider.System.LocalTimeZone;

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period) => TimeProvider.System.CreateTimer(callback, state, dueTime, period);
}
