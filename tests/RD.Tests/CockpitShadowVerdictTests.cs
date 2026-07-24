using FluentAssertions;
using RD.Domain;
using RD.Web.Services;

namespace RD.Tests;

/// <summary>In Shadow phase the decisions ARE the queue — would-X verdicts must flip the cockpit out of AllQuiet.</summary>
public class CockpitShadowVerdictTests
{
    private static readonly DateTimeOffset Now = new(2026, 07, 24, 12, 0, 0, TimeSpan.Zero);

    private static CockpitData FreshData(int openActions = 0, int shadowVerdicts = 0) => new()
    {
        TotalClients = 646,
        MasterClientCount = 177,
        ClientsWithActiveStripeSubscription = 268,
        CampaignsLiveMasterClients = 120,
        OpenActionCount = openActions,
        ShadowVerdictCount = shadowVerdicts,
        CompletedSyncRuns =
        [
            new CompletedSyncFact(ExternalSystem.Stripe, Now.AddMinutes(-5)),
            new CompletedSyncFact(ExternalSystem.Meta, Now.AddMinutes(-5)),
        ],
    };

    [Fact]
    public void Shadow_verdicts_alone_make_the_cockpit_busy()
    {
        var snapshot = CockpitRules.Compute(FreshData(shadowVerdicts: 40), Now);
        snapshot.State.Should().Be(CockpitState.Busy);
        snapshot.QueueCount.Should().Be(40);
    }

    [Fact]
    public void No_actions_and_no_verdicts_is_genuinely_all_quiet()
    {
        CockpitRules.Compute(FreshData(), Now).State.Should().Be(CockpitState.AllQuiet);
    }

    [Fact]
    public void Queue_count_sums_staged_and_shadow()
    {
        CockpitRules.Compute(FreshData(openActions: 3, shadowVerdicts: 7), Now).QueueCount.Should().Be(10);
    }
}
