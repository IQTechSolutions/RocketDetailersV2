using FluentAssertions;
using RD.Domain;

namespace RD.Tests.Arrangement;

/// <summary>
/// The payment-arrangement inference: read cadence + amount from behaviour, and
/// only TRUST it when the rhythm is regular across enough payments — otherwise a
/// human confirms. Grounded in the live finding that most clients pay weekly.
/// </summary>
public class ArrangementInferenceTests
{
    private static (DateTimeOffset, decimal) P(int day, decimal amt)
        => (new DateTimeOffset(2026, 6, day, 0, 0, 0, TimeSpan.Zero), amt);

    [Fact]
    public void Empty_history_is_Unknown()
        => ArrangementInference.Infer([]).Should().Be(new InferredArrangement(null, null, ArrangementStatus.Unknown));

    [Fact]
    public void Single_payment_needs_review_amount_but_no_cadence()
    {
        var r = ArrangementInference.Infer([P(1, 500m)]);
        r.ExpectedAmount.Should().Be(500m);
        r.CadenceDays.Should().BeNull();
        r.Status.Should().Be(ArrangementStatus.NeedsReview);
    }

    [Fact]
    public void Regular_weekly_three_payments_is_inferred()
    {
        var r = ArrangementInference.Infer([P(1, 300m), P(8, 300m), P(15, 300m)]);
        r.Status.Should().Be(ArrangementStatus.Inferred);
        r.CadenceDays.Should().Be(7);
        r.ExpectedAmount.Should().Be(300m);
    }

    [Fact]
    public void Two_regular_payments_snap_cadence_but_are_not_yet_trusted()
    {
        var r = ArrangementInference.Infer([P(1, 300m), P(8, 300m)]);
        r.CadenceDays.Should().Be(7);
        r.Status.Should().Be(ArrangementStatus.NeedsReview); // need >= 3 to auto-trust
    }

    [Fact]
    public void Irregular_gaps_are_not_trusted()
    {
        var r = ArrangementInference.Infer([
            P(1, 300m), P(3, 300m),                                             // 2 days
            (new DateTimeOffset(2026, 7, 3, 0, 0, 0, TimeSpan.Zero), 300m),     // then 30 days
        ]);
        r.Status.Should().Be(ArrangementStatus.NeedsReview);
    }

    [Fact]
    public void Regular_monthly_is_inferred_at_30_days()
    {
        var r = ArrangementInference.Infer([
            (new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero), 1000m),
            (new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero), 1000m),
            (new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero), 1000m),
        ]);
        r.Status.Should().Be(ArrangementStatus.Inferred);
        r.CadenceDays.Should().Be(30);
    }

    [Fact]
    public void Amount_is_the_median_not_skewed_by_a_top_up()
    {
        var r = ArrangementInference.Infer([P(1, 300m), P(8, 300m), P(15, 900m)]);
        r.ExpectedAmount.Should().Be(300m);
    }
}
