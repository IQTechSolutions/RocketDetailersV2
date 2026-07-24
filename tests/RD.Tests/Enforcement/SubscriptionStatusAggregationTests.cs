using FluentAssertions;
using RD.Infrastructure.Enforcement;

namespace RD.Tests.Enforcement;

/// <summary>
/// Multi-account clients own several Stripe subscriptions; the state builder must
/// take the most-ACTIVE status so a paying client isn't read as canceled because
/// one of their other subscriptions lapsed.
/// </summary>
public class SubscriptionStatusAggregationTests
{
    [Fact]
    public void Active_wins_over_canceled()
        => ClientStateBuilder.BestSubscriptionStatus(["canceled", "active"]).Should().Be("active");

    [Fact]
    public void Active_wins_over_past_due()
        => ClientStateBuilder.BestSubscriptionStatus(["past_due", "active"]).Should().Be("active");

    [Fact]
    public void Past_due_beats_canceled_when_no_active_sub()
        => ClientStateBuilder.BestSubscriptionStatus(["canceled", "past_due"]).Should().Be("past_due");

    [Fact]
    public void All_canceled_stays_canceled()
        => ClientStateBuilder.BestSubscriptionStatus(["canceled", "canceled"]).Should().Be("canceled");

    [Fact]
    public void No_subscriptions_is_null()
        => ClientStateBuilder.BestSubscriptionStatus([]).Should().BeNull();

    [Fact]
    public void Nulls_and_blanks_are_ignored()
        => ClientStateBuilder.BestSubscriptionStatus([null, "", "past_due"]).Should().Be("past_due");

    [Fact]
    public void Unknown_status_ranks_last()
        => ClientStateBuilder.BestSubscriptionStatus(["weird_status", "unpaid"]).Should().Be("unpaid");
}
