namespace RD.Domain;

/// <summary>All time-dependent logic (dunning windows, clean days, staleness) goes through this — tests inject fixed clocks.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
