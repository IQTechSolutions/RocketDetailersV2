using FluentAssertions;
using RD.Web.Services;

namespace RD.Tests.Mapping;

/// <summary>
/// Pure-logic tests for the mapping wizard's factored helpers — the promotion
/// guard (which PromoteToAssist delegates to), the blast-radius phrasing, and
/// the fuzzy link suggester. No DB, no rendering.
/// </summary>
public class MappingLogicTests
{
    // ---------------- PromotionGuard ----------------

    [Fact]
    public void CanPromote_refuses_without_a_current_verification()
    {
        var (canPromote, reason) = PromotionGuard.CanPromote(hasCurrentVerification: false, requiredLinksPresent: true);

        canPromote.Should().BeFalse();
        reason.Should().Contain("verify the mapping first");
    }

    [Fact]
    public void CanPromote_refuses_when_required_links_are_missing()
    {
        var (canPromote, reason) = PromotionGuard.CanPromote(hasCurrentVerification: true, requiredLinksPresent: false);

        canPromote.Should().BeFalse();
        reason.Should().Contain("required links");
    }

    [Fact]
    public void CanPromote_missing_links_take_precedence_over_missing_verification()
    {
        var (canPromote, reason) = PromotionGuard.CanPromote(hasCurrentVerification: false, requiredLinksPresent: false);

        canPromote.Should().BeFalse();
        reason.Should().Contain("required links");
    }

    [Fact]
    public void CanPromote_allows_when_verified_and_all_links_present()
    {
        var (canPromote, reason) = PromotionGuard.CanPromote(hasCurrentVerification: true, requiredLinksPresent: true);

        canPromote.Should().BeTrue();
        reason.Should().Contain("Assist");
    }

    // ---------------- BlastRadius ----------------

    private static CampaignFact Campaign(string id, string status, decimal spend)
        => new(id, id, status, DailyBudget: null, RecentDailySpend: spend, Currency: "USD");

    [Fact]
    public void BlastRadius_sums_only_live_campaign_spend()
    {
        var campaigns = new[]
        {
            Campaign("c1", "ACTIVE", 25m),
            Campaign("c2", "ACTIVE", 10m),
            Campaign("c3", "PAUSED", 100m), // excluded — the engine can't be blamed for paused spend
        };

        var summary = BlastRadius.Compute(campaigns, "USD");

        summary.LiveCampaignCount.Should().Be(2);
        summary.DailySpend.Should().Be(35m);
        summary.HasLiveSpend.Should().BeTrue();
        summary.Headline.Should().Contain("2 campaigns");
        summary.Headline.Should().Contain("$35");
        summary.Headline.Should().Contain("/day for this client");
    }

    [Fact]
    public void BlastRadius_describes_the_no_live_campaigns_case_honestly()
    {
        var summary = BlastRadius.Compute([Campaign("c1", "PAUSED", 40m)], "USD");

        summary.LiveCampaignCount.Should().Be(0);
        summary.HasLiveSpend.Should().BeFalse();
        summary.Headline.Should().Contain("No live campaigns");
        summary.Headline.Should().NotContain("$");
    }

    [Fact]
    public void BlastRadius_singularizes_one_campaign()
    {
        var summary = BlastRadius.Compute([Campaign("c1", "ACTIVE", 12m)], "USD");

        summary.Headline.Should().Contain("1 campaign ");
        summary.Headline.Should().NotContain("1 campaigns");
    }

    // ---------------- LinkSuggester ----------------

    [Fact]
    public void Similarity_scores_a_name_match_higher_than_an_unrelated_label()
    {
        var strong = LinkSuggester.Similarity("Sparkle Auto Spa", "Sparkle Auto Spa | Lead Gen");
        var weak = LinkSuggester.Similarity("Sparkle Auto Spa", "Random Campaign 42");

        strong.Should().BeGreaterThan(weak);
        strong.Should().BeGreaterThan(0.5);
        weak.Should().BeLessThan(0.25);
    }

    [Fact]
    public void Rank_puts_the_best_name_match_first_and_labels_it()
    {
        var candidates = new[]
        {
            new SuggestionCandidate("camp_random", "Winter Blowout", DateTimeOffset.UtcNow),
            new SuggestionCandidate("camp_match", "Acme Detailing — Spring Promo", DateTimeOffset.UtcNow),
            new SuggestionCandidate("camp_none", "Unrelated 99", DateTimeOffset.UtcNow),
        };

        var ranked = LinkSuggester.Rank("Acme Detailing", candidates);

        ranked.Should().HaveCount(3);
        ranked[0].ExternalId.Should().Be("camp_match");
        ranked[0].Rationale.Should().Contain("match");
        ranked[0].Score.Should().BeGreaterThan(ranked[1].Score);
    }

    [Fact]
    public void Rank_breaks_score_ties_by_sync_recency()
    {
        var older = new DateTimeOffset(2026, 07, 20, 0, 0, 0, TimeSpan.Zero);
        var newer = new DateTimeOffset(2026, 07, 24, 0, 0, 0, TimeSpan.Zero);
        var candidates = new[]
        {
            new SuggestionCandidate("cus_old", "cus_old", older),   // no name signal → score 0
            new SuggestionCandidate("cus_new", "cus_new", newer),   // no name signal → score 0
        };

        var ranked = LinkSuggester.Rank("Nothing Alike Ltd", candidates);

        ranked[0].ExternalId.Should().Be("cus_new");
    }
}
