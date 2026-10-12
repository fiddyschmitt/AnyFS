namespace AnyFS.Sdk.Tests;

/// <summary>A clock the test moves by hand (timestamps and wall time advance together).</summary>
public sealed class FakeTime : TimeProvider
{
    private long _ticks = new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero).UtcTicks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Volatile.Read(ref _ticks);

    public override DateTimeOffset GetUtcNow() => new(Volatile.Read(ref _ticks), TimeSpan.Zero);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}
