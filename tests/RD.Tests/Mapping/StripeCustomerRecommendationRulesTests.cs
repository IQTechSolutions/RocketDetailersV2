using FluentAssertions;
using RD.Web.Services;

namespace RD.Tests.Mapping;

public class StripeCustomerRecommendationRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 7, 20, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan FreshnessBound = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan InvoiceLookback = TimeSpan.FromDays(30);

    [Fact]
    public void Recommends_the_only_active_customer()
    {
        var decision = Recommend(
            Candidate("cus_old", ["canceled"]),
            Candidate("cus_current", ["active"]));

        decision.Should().Be(new StripeCustomerRecommendation(
            "cus_current", StripeCustomerRecommendationReason.ActiveOrTrialingSubscription));
        decision.IsRecommendation.Should().BeTrue();
    }

    [Fact]
    public void Recommends_the_only_trialing_customer_case_insensitively()
    {
        var decision = Recommend(Candidate("cus_trial", ["  TRIALING  "]));

        decision.RecommendedExternalId.Should().Be("cus_trial");
        decision.Reason.Should().Be(StripeCustomerRecommendationReason.ActiveOrTrialingSubscription);
    }

    [Fact]
    public void Recommends_the_only_customer_with_a_recent_paid_subscription_invoice()
    {
        var decision = Recommend(
            Candidate("cus_old", ["canceled"], Now.AddDays(-31)),
            Candidate("cus_paid", ["canceled"], Now.AddDays(-2)));

        decision.Should().Be(new StripeCustomerRecommendation(
            "cus_paid", StripeCustomerRecommendationReason.RecentPaidSubscriptionInvoice));
    }

    [Fact]
    public void Includes_paid_invoice_exactly_on_the_lookback_boundary()
    {
        var decision = Recommend(Candidate("cus_paid", ["canceled"], Now - InvoiceLookback));

        decision.RecommendedExternalId.Should().Be("cus_paid");
    }

    [Theory]
    [InlineData("canceled")]
    [InlineData("incomplete_expired")]
    public void Terminal_subscription_on_another_customer_does_not_create_billing_ambiguity(string status)
    {
        var decision = Recommend(
            Candidate("cus_current", ["active"]),
            Candidate("cus_terminal", [status]));

        decision.RecommendedExternalId.Should().Be("cus_current");
    }

    [Theory]
    [InlineData("past_due")]
    [InlineData("unpaid")]
    [InlineData("incomplete")]
    [InlineData("paused")]
    [InlineData("unknown_new_stripe_status")]
    public void Any_nonterminal_status_on_multiple_customers_is_ambiguous(string otherStatus)
    {
        var decision = Recommend(
            Candidate("cus_active", ["active"]),
            Candidate("cus_other", [otherStatus]));

        decision.Should().Be(new StripeCustomerRecommendation(
            null, StripeCustomerRecommendationReason.MultipleBillingOwners));
    }

    [Fact]
    public void Multiple_recent_paid_owners_are_ambiguous_even_when_one_is_more_recent()
    {
        var decision = Recommend(
            Candidate("cus_a", ["canceled"], Now.AddDays(-10)),
            Candidate("cus_b", ["canceled"], Now.AddDays(-1)));

        decision.Reason.Should().Be(StripeCustomerRecommendationReason.MultipleRecentPaidOwners);
        decision.IsRecommendation.Should().BeFalse();
    }

    [Fact]
    public void Active_owner_conflicting_with_recent_paid_owner_is_ambiguous()
    {
        var decision = Recommend(
            Candidate("cus_active", ["active"]),
            Candidate("cus_paid", ["canceled"], Now.AddDays(-1)));

        decision.Reason.Should().Be(StripeCustomerRecommendationReason.ConflictingSubscriptionAndPaidOwners);
        decision.RecommendedExternalId.Should().BeNull();
    }

    [Fact]
    public void Matching_active_and_recent_paid_evidence_recommends_that_customer()
    {
        var decision = Recommend(Candidate("cus_a", ["active"], Now.AddDays(-1)));

        decision.Should().Be(new StripeCustomerRecommendation(
            "cus_a", StripeCustomerRecommendationReason.ActiveOrTrialingSubscription));
    }

    [Theory]
    [InlineData("past_due")]
    [InlineData("unpaid")]
    [InlineData("incomplete")]
    [InlineData("paused")]
    [InlineData("unknown_new_stripe_status")]
    public void Sole_nonterminal_owner_conflicting_with_a_different_paid_owner_is_ambiguous(string status)
    {
        var decision = Recommend(
            Candidate("cus_billing", [status]),
            Candidate("cus_paid", ["canceled"], Now.AddDays(-1)));

        decision.Reason.Should().Be(StripeCustomerRecommendationReason.ConflictingSubscriptionAndPaidOwners);
        decision.IsRecommendation.Should().BeFalse();
    }

    [Fact]
    public void Missing_completed_sync_abstains()
    {
        var input = Input(null, Candidate("cus_a", ["active"]));

        var decision = StripeCustomerRecommendationRules.Recommend(
            input, Now, FreshnessBound, InvoiceLookback);

        decision.Reason.Should().Be(StripeCustomerRecommendationReason.NoCompletedStripeSync);
        decision.IsRecommendation.Should().BeFalse();
    }

    [Fact]
    public void Stale_completed_sync_abstains()
    {
        var input = Input(Now - FreshnessBound - TimeSpan.FromTicks(1), Candidate("cus_a", ["active"]));

        var decision = StripeCustomerRecommendationRules.Recommend(
            input, Now, FreshnessBound, InvoiceLookback);

        decision.Reason.Should().Be(StripeCustomerRecommendationReason.StaleCompletedStripeSync);
    }

    [Fact]
    public void Sync_exactly_on_the_freshness_boundary_is_accepted()
    {
        var input = Input(Now - FreshnessBound, Candidate("cus_a", ["active"]));

        var decision = StripeCustomerRecommendationRules.Recommend(
            input, Now, FreshnessBound, InvoiceLookback);

        decision.RecommendedExternalId.Should().Be("cus_a");
    }

    [Fact]
    public void Future_completed_sync_abstains()
    {
        var input = Input(Now.AddTicks(1), Candidate("cus_a", ["active"]));

        var decision = StripeCustomerRecommendationRules.Recommend(
            input, Now, FreshnessBound, InvoiceLookback);

        decision.Reason.Should().Be(StripeCustomerRecommendationReason.FutureCompletedStripeSync);
    }

    [Fact]
    public void Future_and_out_of_lookback_invoice_timestamps_are_not_positive_evidence()
    {
        var decision = Recommend(Candidate(
            "cus_a", ["canceled"], Now.AddTicks(1), Now - InvoiceLookback - TimeSpan.FromTicks(1)));

        decision.Should().Be(new StripeCustomerRecommendation(
            null, StripeCustomerRecommendationReason.NoPositiveEvidence));
    }

    [Fact]
    public void Past_due_without_recent_paid_evidence_is_not_a_positive_recommendation()
    {
        var decision = Recommend(Candidate("cus_a", ["past_due"]));

        decision.Reason.Should().Be(StripeCustomerRecommendationReason.NoPositiveEvidence);
        decision.IsRecommendation.Should().BeFalse();
    }

    [Fact]
    public void No_subscription_or_paid_invoice_evidence_abstains()
    {
        var decision = Recommend(Candidate("cus_a", []));

        decision.Reason.Should().Be(StripeCustomerRecommendationReason.NoPositiveEvidence);
    }

    [Fact]
    public void Result_is_independent_of_candidate_status_and_invoice_input_order()
    {
        var first = Recommend(
            Candidate("cus_z", ["canceled"], Now.AddDays(-45)),
            Candidate("cus_a", ["canceled", "active"], Now.AddDays(-2), Now.AddDays(-5)));
        var reordered = Recommend(
            Candidate("cus_a", ["active", "canceled"], Now.AddDays(-5), Now.AddDays(-2)),
            Candidate("cus_z", ["canceled"], Now.AddDays(-45)));

        reordered.Should().Be(first);
    }

    [Fact]
    public void Duplicate_rows_for_one_customer_are_merged_without_creating_ambiguity()
    {
        var decision = Recommend(
            Candidate("cus_a", ["canceled"], Now.AddDays(-2)),
            Candidate("cus_a", ["active"]));

        decision.Should().Be(new StripeCustomerRecommendation(
            "cus_a", StripeCustomerRecommendationReason.ActiveOrTrialingSubscription));
    }

    [Fact]
    public void Negative_time_windows_are_rejected()
    {
        var input = Input(Now, Candidate("cus_a", ["active"]));

        var actFreshness = () => StripeCustomerRecommendationRules.Recommend(
            input, Now, TimeSpan.FromTicks(-1), InvoiceLookback);
        var actLookback = () => StripeCustomerRecommendationRules.Recommend(
            input, Now, FreshnessBound, TimeSpan.FromTicks(-1));

        actFreshness.Should().Throw<ArgumentOutOfRangeException>();
        actLookback.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static StripeCustomerRecommendation Recommend(params StripeCustomerRecommendationEvidence[] candidates)
        => StripeCustomerRecommendationRules.Recommend(
            Input(Now.AddMinutes(-5), candidates), Now, FreshnessBound, InvoiceLookback);

    private static StripeCustomerRecommendationInput Input(
        DateTimeOffset? syncAt,
        params StripeCustomerRecommendationEvidence[] candidates)
        => new(syncAt, candidates);

    private static StripeCustomerRecommendationEvidence Candidate(
        string externalId,
        IReadOnlyList<string> statuses,
        params DateTimeOffset[] paidAt)
        => new(externalId, statuses, paidAt);
}
