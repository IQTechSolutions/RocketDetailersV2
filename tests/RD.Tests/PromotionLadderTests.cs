using FluentAssertions;
using RD.Domain;
using RD.Infrastructure.Enforcement;

namespace RD.Tests;

/// <summary>The Auto gate: only a mapped, exercised Assist client with an unbroken clean-day streak may be promoted.</summary>
public class PromotionLadderTests
{
    private static readonly DateTimeOffset Now = new(2026, 08, 15, 12, 0, 0, TimeSpan.Zero);
    private const int Threshold = 14;

    private static PromotionLadder.Assessment Assess(
        EnforcementMode mode = EnforcementMode.Assist,
        DateTimeOffset? assistSince = null,
        IReadOnlyCollection<DateOnly>? unclean = null,
        bool exercised = true,
        bool mappingVerified = true,
        bool killEngaged = false)
        => PromotionLadder.Assess(mode, assistSince ?? Now.AddDays(-30), unclean ?? [], exercised, mappingVerified, killEngaged, Now, Threshold);

    [Fact]
    public void Clean_exercised_verified_assist_client_can_be_promoted()
    {
        var a = Assess();
        a.CanPromote.Should().BeTrue();
        a.CleanDayStreak.Should().Be(Threshold);
        a.Blockers.Should().BeEmpty();
    }

    [Fact]
    public void An_unclean_day_inside_the_window_breaks_the_streak()
    {
        var unclean = new[] { DateOnly.FromDateTime(Now.UtcDateTime).AddDays(-3) };
        var a = Assess(unclean: unclean);
        a.CleanDayStreak.Should().Be(3); // today, -1, -2 clean; -3 unclean
        a.CanPromote.Should().BeFalse();
        a.Blockers.Should().Contain(b => b.Contains("3 of 14"));
    }

    [Fact]
    public void Without_an_exercised_failure_path_promotion_is_blocked()
    {
        var a = Assess(exercised: false);
        a.CanPromote.Should().BeFalse();
        a.Blockers.Should().Contain(b => b.Contains("exercised"));
    }

    [Fact]
    public void Unverified_mapping_blocks_promotion()
        => Assess(mappingVerified: false).Blockers.Should().Contain(b => b.Contains("Mapping is not verified"));

    [Fact]
    public void Only_assist_clients_are_eligible()
    {
        Assess(mode: EnforcementMode.Shadow).CanPromote.Should().BeFalse();
        Assess(mode: EnforcementMode.Auto).Blockers.Should().Contain(b => b.Contains("Auto mode"));
    }

    [Fact]
    public void Kill_switch_blocks_promotion()
        => Assess(killEngaged: true).Blockers.Should().Contain(b => b.Contains("kill switch"));

    [Fact]
    public void A_client_newly_in_assist_cannot_reach_the_threshold_yet()
    {
        // Entered Assist 5 days ago — even with zero unclean days the streak caps at 5.
        var a = Assess(assistSince: Now.AddDays(-5));
        a.CleanDayStreak.Should().Be(6); // days -5..0 inclusive
        a.CanPromote.Should().BeFalse();
    }

    [Fact]
    public void No_assist_history_means_zero_streak()
        => PromotionLadder.Assess(EnforcementMode.Assist, assistSince: null, [], true, true, false, Now, Threshold)
            .CleanDayStreak.Should().Be(0);
}
