using FluentAssertions;
using RD.Domain;
using RD.Tools.Import;
using RD.Web.Services;

namespace RD.Tests;

public sealed class StripeDiscoveryClassificationTests
{
    [Fact]
    public void Delinquent_customer_uses_a_Stripe_specific_investigation_kind()
    {
        StripeDiscoveryRunner.DelinquentInvestigationKind
            .Should().Be(InvestigationKind.StripeCustomerDelinquent);
        InvestigationKind.StripeCustomerDelinquent.Title()
            .Should().Be("Stripe customer is delinquent");
        InvestigationKind.StripeCustomerDelinquent.WhyItMatters()
            .Should().Contain("unpaid balance");
    }
}
