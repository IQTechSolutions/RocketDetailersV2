using RD.Domain;

namespace RD.Tests.Integration.TestInfra;

/// <summary>Fixed clock — sync tests are deterministic, never wall-clock dependent.</summary>
public sealed class TestClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
}
