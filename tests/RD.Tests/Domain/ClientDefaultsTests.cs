using FluentAssertions;
using RD.Domain;
using RD.Domain.Entities;
using RD.Domain.Policy;

namespace RD.Tests.Domain;

/// <summary>
/// Guards the safe default on <see cref="Client.AccountType"/>. The AccountType
/// enum's zero value is Master (the ad-spend-enforcement-ACTIVE type), so a Client
/// created without setting AccountType must NOT silently land in enforcement.
/// The entity defaults it to Own; Master is only ever an explicit human decision.
/// </summary>
public class ClientDefaultsTests
{
    [Fact]
    public void New_client_defaults_to_own_account_not_master()
    {
        var client = new Client { BusinessName = "Unset Detailing Co" };

        client.AccountType.Should().Be(AccountType.Own,
            "a client created without an explicit AccountType must never be silently enforcement-active");
    }

    [Fact]
    public void Defaulted_account_type_is_enforcement_inert_even_when_unpaid()
    {
        // A brand-new client whose account type was never set, dropped into a
        // scenario that WOULD pause a master-account client (unpaid subscription).
        var defaulted = new Client { BusinessName = "Unset Detailing Co" };

        var state = new ClientState
        {
            ClientId = Guid.NewGuid(),
            Mode = EnforcementMode.Shadow,
            Contract = ContractType.Paid,
            Account = defaulted.AccountType, // flows through as it would via ClientStateBuilder
            CurrencyCode = "USD",
            MappingVerified = true,
            SubscriptionStatus = "unpaid",
            EvaluatedAt = new DateTimeOffset(2026, 07, 25, 12, 0, 0, TimeSpan.Zero),
        };

        // Rule 2 (own-account short-circuit) must win over the unpaid-subscription
        // pause — the default keeps a client out of ad-spend enforcement.
        EligibilityPolicy.Evaluate(state).Action.Should().Be(ProposedActionType.None);
    }
}
