using System.Diagnostics;

namespace GarageGames.V2;

public interface IMonotonicClock
{
    long MonotonicMilliseconds { get; }
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemMonotonicClock : IMonotonicClock
{
    private readonly long _originTicks = Stopwatch.GetTimestamp();
    private readonly DateTimeOffset _originUtc = DateTimeOffset.UtcNow;

    public long MonotonicMilliseconds => (long)((Stopwatch.GetTimestamp() - _originTicks) * 1000d / Stopwatch.Frequency);
    public DateTimeOffset UtcNow => _originUtc.AddMilliseconds(MonotonicMilliseconds);
}

public sealed class SimulationClock : IMonotonicClock
{
    private readonly SystemMonotonicClock _system = new();
    private long _offsetMilliseconds;

    public long MonotonicMilliseconds => _system.MonotonicMilliseconds + Interlocked.Read(ref _offsetMilliseconds);
    public DateTimeOffset UtcNow => _system.UtcNow.AddMilliseconds(Interlocked.Read(ref _offsetMilliseconds));

    public void Advance(TimeSpan amount)
    {
        if (amount < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "Simulation time cannot move backwards.");
        }

        Interlocked.Add(ref _offsetMilliseconds, (long)amount.TotalMilliseconds);
    }
}

public sealed class TestClock : IMonotonicClock
{
    private long _milliseconds;
    private readonly DateTimeOffset _originUtc;

    public TestClock(DateTimeOffset? originUtc = null)
    {
        _originUtc = originUtc ?? DateTimeOffset.UtcNow;
    }

    public long MonotonicMilliseconds => Interlocked.Read(ref _milliseconds);
    public DateTimeOffset UtcNow => _originUtc.AddMilliseconds(MonotonicMilliseconds);

    public void Advance(TimeSpan amount)
    {
        if (amount < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "Test time cannot move backwards.");
        }

        Interlocked.Add(ref _milliseconds, (long)amount.TotalMilliseconds);
    }
}
