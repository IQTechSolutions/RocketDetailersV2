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
        new(AccountType.Own, "USD", HasPackage: true, EffectiveStripePriceId: "price_abc", StripeCustomerId: customer);

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
                EffectiveStripePriceId: null, StripeCustomerId: null));

        d.Ready.Should().BeFalse();
        d.Blockers.Should().HaveCount(3); // master + non-USD + no price
    }

    [Fact]
    public void Multiple_customers_without_a_preference_block_the_draft_instead_of_creating_another_customer()
    {
        var draft = ConvertDrafter.Draft(new ConvertDraftInput(
            AccountType.Own, "USD", HasPackage: true, EffectiveStripePriceId: "price_abc",
            StripeCustomerId: null, HasAmbiguousStripeCustomers: true));

        draft.Ready.Should().BeFalse();
        draft.WouldCreateCustomer.Should().BeFalse();
        draft.StripeCustomerId.Should().BeNull();
        draft.Blockers.Should().ContainSingle(b => b.Contains("preferred billing customer"));
    }

    [Fact]
    public void Open_customer_ownership_investigation_blocks_billing_even_with_a_preference()
    {
        var draft = ConvertDrafter.Draft(OwnReady() with { HasOpenStripeOwnershipInvestigation = true });

        draft.Ready.Should().BeFalse();
        draft.StripeCustomerId.Should().Be("cus_123");
        draft.Blockers.Should().ContainSingle(b => b.Contains("ownership is still unconfirmed"));
    }

    [Fact]
    public void Existing_current_subscription_blocks_creation_of_a_second_subscription()
    {
        var draft = ConvertDrafter.Draft(OwnReady() with { HasExistingNonTerminalStripeSubscription = true });

        draft.Ready.Should().BeFalse();
        draft.Blockers.Should().ContainSingle(b => b.Contains("creating a second subscription"));
    }

    [Fact]
    public void Subscription_without_a_matching_customer_link_blocks_billing()
    {
        var draft = ConvertDrafter.Draft(OwnReady() with { HasSubscriptionWithoutCustomerLink = true });

        draft.Ready.Should().BeFalse();
        draft.Blockers.Should().ContainSingle(b => b.Contains("no matching active customer link"));
    }

    [Fact]
    public void Missing_fresh_Stripe_evidence_blocks_billing()
    {
        var draft = ConvertDrafter.Draft(OwnReady() with { StripeEvidenceIsFresh = false });

        draft.Ready.Should().BeFalse();
        draft.Blockers.Should().ContainSingle(b => b.Contains("complete a Stripe sync"));
    }
}
