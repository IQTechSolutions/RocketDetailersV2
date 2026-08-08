using System.Text.Json;
using FluentAssertions;
using RD.Domain;

namespace RD.Tests.Mapping;

public class MappingVerificationCoverageTests
{
    [Fact]
    public void Pins_all_current_link_versions()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var json = JsonSerializer.Serialize(new[]
        {
            new { linkId = a, linkVersion = 1 },
            new { linkId = b, linkVersion = 3 },
        });

        MappingVerificationCoverage.PinsAll(json, [(a, 1), (b, 3)]).Should().BeTrue();
    }

    [Fact]
    public void Missing_additional_link_fails_closed()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var json = JsonSerializer.Serialize(new[] { new { linkId = a, linkVersion = 1 } });

        MappingVerificationCoverage.PinsAll(json, [(a, 1), (b, 1)]).Should().BeFalse();
    }

    [Fact]
    public void Changed_link_version_fails_closed()
    {
        var id = Guid.NewGuid();
        var json = JsonSerializer.Serialize(new[] { new { linkId = id, linkVersion = 1 } });

        MappingVerificationCoverage.PinsAll(json, [(id, 2)]).Should().BeFalse();
    }

    [Fact]
    public void Extra_stale_pin_fails_closed_after_a_link_is_removed_or_moved()
    {
        var current = Guid.NewGuid();
        var moved = Guid.NewGuid();
        var json = JsonSerializer.Serialize(new[]
        {
            new { linkId = current, linkVersion = 1 },
            new { linkId = moved, linkVersion = 4 },
        });

        MappingVerificationCoverage.PinsAll(json, [(current, 1)]).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("[1]")]
    public void Missing_or_malformed_snapshot_fails_closed(string? json)
    {
        MappingVerificationCoverage.PinsAll(json, [(Guid.NewGuid(), 1)]).Should().BeFalse();
    }
}
