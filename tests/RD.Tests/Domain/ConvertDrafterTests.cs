using FluentAssertions;
using RD.Domain;

namespace RD.Tests.Domain;

/// <summary>
/// Golden tests for the pure <see cref="ConvertDrafter"/> (A1 / Shadow). Own-account + USD + a
/// package with a Stripe price is auto-draftable; everything else surfaces an explicit blocker so
/// the operator sees exactly why a conversion can't be auto-drafted. No I/O — same replayable
/// pure-function pattern as EligibilityPolicy.
/// </summary>
public class ConvertDrafterTests
{
    private static ConvertDraftInput OwnReady(string? customer = "cus_123") =>
        new(AccountType.Own, "USD", HasPackage: true, EffectiveStripePriceId: "price_abc", FirstStripeCustomerId: customer);

    [Fact]
    public void Own_account_usd_with_price_and_customer_is_ready()
    {
        var d = ConvertDrafter.Draft(OwnReady());

        d.Ready.Should().BeTrue();
        d.Blockers.Should().BeEmpty();
        d.StripePriceId.Should().Be("price_abc");
        d.StripeCustomerId.Should().Be("cus_123");
        d.WouldCreateCustomer.Should().BeFalse();
        d.Summary.Should().Contain("cus_123").And.Contain("price_abc");
    }

    [Fact]
    public void No_customer_means_the_app_would_create_one()
    {
        var d = ConvertDrafter.Draft(OwnReady(customer: null));

        d.Ready.Should().BeTrue();
        d.WouldCreateCustomer.Should().BeTrue();
        d.StripeCustomerId.Should().BeNull();
        d.Summary.Should().Contain("new Stripe customer");
    }

    [Fact]
    public void Missing_stripe_price_blocks_the_draft()
    {
        var d = ConvertDrafter.Draft(OwnReady() with { EffectiveStripePriceId = null });

        d.Ready.Should().BeFalse();
        d.StripePriceId.Should().BeNull();
        d.Blockers.Should().ContainSingle().Which.Should().Contain("Stripe price");
    }

    [Fact]
    public void No_package_blocks_the_draft()
    {
        var d = ConvertDrafter.Draft(OwnReady() with { HasPackage = false, EffectiveStripePriceId = null });

        d.Ready.Should().BeFalse();
        d.Blockers.Should().ContainSingle().Which.Should().Contain("No package");
    }

    [Fact]
    public void Master_account_is_not_automated_yet()
    {
        var d = ConvertDrafter.Draft(OwnReady() with { AccountType = AccountType.Master });

        d.Ready.Should().BeFalse();
        d.Blockers.Should().Contain(b => b.Contains("Master-account"));
    }

    [Fact]
    public void Non_usd_currency_is_blocked()
    {
        var d = ConvertDrafter.Draft(OwnReady() with { CurrencyCode = "ZAR" });

        d.Ready.Should().BeFalse();
        d.Blockers.Should().Contain(b => b.Contains("USD-only"));
    }

    [Fact]
    public void Multiple_blockers_all_surface()
    {
        var d = ConvertDrafter.Draft(
            new ConvertDraftInput(AccountType.Master, "ZAR", HasPackage: true,
                EffectiveStripePriceId: null, FirstStripeCustomerId: null));

        d.Ready.Should().BeFalse();
        d.Blockers.Should().HaveCount(3); // master + non-USD + no price
    }
}
