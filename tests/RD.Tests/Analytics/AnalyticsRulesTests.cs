using FluentAssertions;
using RD.Domain;
using RD.Web.Services;

namespace RD.Tests.Analytics;

/// <summary>
/// Pure-logic tests for the owner-analytics derivation (M3): net cash position
/// per master client, per-currency bucketing (no FX), exposure floor, package
/// revenue mix, the enforcement-activity time series, and verified-mapping %.
/// No DB, no rendering — constructed data only, exactly like CockpitRulesTests.
/// </summary>
public class AnalyticsRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 07, 24, 12, 00, 00, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 07, 24);

    private static MasterClientFact Master(Guid id, string currency = "USD",
        EnforcementMode mode = EnforcementMode.Shadow, string? package = null, string name = "Acme Detailing")
        => new(id, name, currency, mode, package);

    private static LedgerFact Led(Guid id, LedgerEntryType type, decimal signed,
        string currency = "USD", DateTimeOffset? at = null)
        => new(id, currency, type, signed, at ?? Now.AddHours(-1));

    // ---------------- Net cash position: sign + per-currency bucketing ----------------

    [Fact]
    public void Net_cash_position_is_paid_minus_ad_spend_signed_per_client()
    {
        var client = Guid.NewGuid(); // +300 paid, 200 ad spend → +100
        var data = new AnalyticsData
        {
            MasterClientCount = 1,
            MasterClients = [Master(client)],
            MasterClientLedger =
            [
                Led(client, LedgerEntryType.ChargePaid, 300m),
                Led(client, LedgerEntryType.AdSpend, -200m),
            ],
        };

        var s = AnalyticsRules.Compute(data, Now);

        var pos = s.ClientNetPositions.Single();
        pos.Paid.Should().Be(300m);
        pos.AdSpendCost.Should().Be(200m);
        pos.NetPosition.Should().Be(100m);
        s.UsdRollup.NetPosition.Should().Be(100m);
        s.HasLedgerData.Should().BeTrue();
    }

    [Fact]
    public void Non_usd_client_is_kept_in_its_own_currency_bucket_with_no_fx()
    {
        var usd = Guid.NewGuid(); // +300 − 200 = +100 USD
        var cad = Guid.NewGuid(); // +100 − 250 = −150 CAD
        var data = new AnalyticsData
        {
            MasterClientCount = 2,
            MasterClients = [Master(usd, "USD"), Master(cad, "CAD")],
            MasterClientLedger =
            [
                Led(usd, LedgerEntryType.ChargePaid, 300m),
                Led(usd, LedgerEntryType.AdSpend, -200m),
                Led(cad, LedgerEntryType.ChargePaid, 100m, "CAD"),
                Led(cad, LedgerEntryType.AdSpend, -250m, "CAD"),
            ],
        };

        var s = AnalyticsRules.Compute(data, Now);

        s.NetPositionByCurrency[0].CurrencyCode.Should().Be("USD", "USD always leads");
        s.UsdRollup.NetPosition.Should().Be(100m);
        var cadRollup = s.NetPositionByCurrency.Single(r => r.CurrencyCode == "CAD");
        cadRollup.NetPosition.Should().Be(-150m, "currencies never mix — no FX conversion");
        cadRollup.NegativeClientCount.Should().Be(1);
    }

    [Fact]
    public void Client_net_positions_are_ranked_most_negative_first()
    {
        var costly = Guid.NewGuid();   // −400
        var profitable = Guid.NewGuid(); // +250
        var data = new AnalyticsData
        {
            MasterClientCount = 2,
            MasterClients = [Master(profitable), Master(costly)],
            MasterClientLedger =
            [
                Led(profitable, LedgerEntryType.ChargePaid, 250m),
                Led(costly, LedgerEntryType.AdSpend, -400m),
            ],
        };

        var s = AnalyticsRules.Compute(data, Now);

        s.ClientNetPositions[0].ClientId.Should().Be(costly, "the clients costing money lead");
        s.ClientNetPositions[0].NetPosition.Should().Be(-400m);
        s.ClientNetPositions[1].ClientId.Should().Be(profitable);
    }

    [Fact]
    public void Master_clients_with_no_ledger_activity_are_excluded_from_the_ranked_list()
    {
        var active = Guid.NewGuid();
        var idle = Guid.NewGuid();
        var data = new AnalyticsData
        {
            MasterClientCount = 2,
            MasterClients = [Master(active), Master(idle)],
            MasterClientLedger = [Led(active, LedgerEntryType.ChargePaid, 50m)],
        };

        var s = AnalyticsRules.Compute(data, Now);

        s.ClientNetPositions.Should().ContainSingle(p => p.ClientId == active);
    }

    // ---------------- Exposure: floored at zero per client ----------------

    [Fact]
    public void Exposure_floors_at_zero_per_client_then_sums_per_currency()
    {
        var underwater = Guid.NewGuid(); // spent 100, paid 40 → 60 exposed
        var ahead = Guid.NewGuid();      // spent 10, paid 50 → 0 (never negative)
        var data = new AnalyticsData
        {
            MasterClientCount = 2,
            MasterClients = [Master(underwater), Master(ahead)],
            MasterClientLedger =
            [
                Led(underwater, LedgerEntryType.AdSpend, -100m),
                Led(underwater, LedgerEntryType.ChargePaid, 40m),
                Led(ahead, LedgerEntryType.AdSpend, -10m),
                Led(ahead, LedgerEntryType.ChargePaid, 50m),
            ],
        };

        var s = AnalyticsRules.Compute(data, Now);

        var usd = s.ExposureByCurrency.Single(e => e.CurrencyCode == "USD");
        usd.Amount.Should().Be(60m, "the ahead client's overpayment does not offset the underwater client");
        usd.ClientCount.Should().Be(1);
        s.UsdRollup.Exposure.Should().Be(60m);
    }

    [Fact]
    public void Empty_ledger_still_reports_a_zero_usd_rollup_and_exposure()
    {
        var s = AnalyticsRules.Compute(new AnalyticsData(), Now);

        s.HasLedgerData.Should().BeFalse();
        s.NetPositionByCurrency.Should().ContainSingle(r => r.CurrencyCode == "USD");
        s.UsdRollup.NetPosition.Should().Be(0m);
        s.ExposureByCurrency.Should().ContainSingle(e => e.CurrencyCode == "USD");
        s.UsdExposure.Should().Be(0m);
    }

    // ---------------- Package / offer revenue mix ----------------

    [Fact]
    public void Package_revenue_groups_charges_paid_with_an_honest_unassigned_bucket()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid();
        var data = new AnalyticsData
        {
            MasterClientCount = 3,
            MasterClients =
            [
                Master(a, package: "Starter"),
                Master(b, package: "Starter"),
                Master(c, package: null), // no package assigned
            ],
            MasterClientLedger =
            [
                Led(a, LedgerEntryType.ChargePaid, 100m),
                Led(b, LedgerEntryType.ChargePaid, 50m),
                Led(c, LedgerEntryType.ChargePaid, 30m),
            ],
        };

        var s = AnalyticsRules.Compute(data, Now);

        s.PackageRevenue.Should().Contain(x => x.PackageName == "Starter" && x.Paid == 150m && x.ClientCount == 2);
        s.PackageRevenue.Should().Contain(x => x.PackageName == AnalyticsRules.UnassignedPackage && x.Paid == 30m);
        s.PackageRevenue[0].PackageName.Should().Be("Starter", "biggest slice first");
    }

    [Fact]
    public void Package_revenue_excludes_clients_with_no_paid_charges()
    {
        var spender = Guid.NewGuid(); // only ad spend, never paid
        var data = new AnalyticsData
        {
            MasterClientCount = 1,
            MasterClients = [Master(spender, package: "Starter")],
            MasterClientLedger = [Led(spender, LedgerEntryType.AdSpend, -80m)],
        };

        var s = AnalyticsRules.Compute(data, Now);

        s.PackageRevenue.Should().BeEmpty("no charges paid means no revenue to attribute");
    }

    // ---------------- Enforcement-activity time series ----------------

    [Fact]
    public void Enforcement_activity_buckets_decisions_by_day_within_the_window()
    {
        var data = new AnalyticsData
        {
            DecisionsByDay =
            [
                new(Today, ProposedActionType.Pause, 3),
                new(Today.AddDays(-1), ProposedActionType.None, 5),
                new(Today.AddDays(-1), ProposedActionType.Pause, 2),
                new(Today.AddDays(-10), ProposedActionType.Pause, 99), // outside a 7-day window → ignored
            ],
        };

        var s = AnalyticsRules.Compute(data, Now, windowDays: 7);

        var act = s.EnforcementActivity;
        act.Days.Should().HaveCount(7);
        act.Days[^1].Should().Be(Today);
        act.Days[0].Should().Be(Today.AddDays(-6));
        act.HasAny.Should().BeTrue();

        var pause = act.Series.Single(x => x.Action == ProposedActionType.Pause);
        pause.CountsPerDay[^1].Should().Be(3, "today");
        pause.CountsPerDay[^2].Should().Be(2, "yesterday");
        pause.CountsPerDay.Sum().Should().Be(5, "the day 10 back is outside the window");

        var none = act.Series.Single(x => x.Action == ProposedActionType.None);
        none.CountsPerDay[^2].Should().Be(5);

        act.Series[0].Action.Should().Be(ProposedActionType.None, "series follow the canonical action order");
    }

    [Fact]
    public void Enforcement_activity_is_empty_when_no_decisions_exist()
    {
        var s = AnalyticsRules.Compute(new AnalyticsData(), Now, windowDays: 14);

        s.EnforcementActivity.Days.Should().HaveCount(14);
        s.EnforcementActivity.Series.Should().BeEmpty();
        s.EnforcementActivity.HasAny.Should().BeFalse();
    }

    // ---------------- Verified-mapping % + client mix ----------------

    [Fact]
    public void Verified_mapping_percent_is_verified_over_total_master_clients()
    {
        var data = new AnalyticsData { MasterClientCount = 200, VerifiedMasterClientCount = 50 };

        AnalyticsRules.Compute(data, Now).VerifiedMappingPct.Should().Be(25.0);
    }

    [Fact]
    public void Verified_mapping_percent_is_zero_with_no_master_clients_no_divide_by_zero()
    {
        AnalyticsRules.Compute(new AnalyticsData(), Now).VerifiedMappingPct.Should().Be(0);
    }

    [Fact]
    public void Mode_mix_reports_every_ladder_rung_even_when_a_rung_is_empty()
    {
        var data = new AnalyticsData
        {
            ClientSegments =
            [
                new(AccountType.Master, ContractType.Trial, 10),
                new(AccountType.Master, ContractType.Paid, 20),
                new(AccountType.Own, ContractType.Paid, 5),
            ],
            MasterModes =
            [
                new(EnforcementMode.Shadow, 25),
                new(EnforcementMode.Assist, 5),
                // Auto intentionally absent
            ],
        };

        var s = AnalyticsRules.Compute(data, Now);

        s.ClientSegments.Should().HaveCount(3);
        s.TotalClients.Should().Be(35);
        s.ModeMix.Should().HaveCount(3);
        s.ModeMix.Single(m => m.Mode == EnforcementMode.Auto).Count.Should().Be(0);
        s.ModeMix.Single(m => m.Mode == EnforcementMode.Shadow).Count.Should().Be(25);
    }

    [Fact]
    public void Mapping_gaps_are_passed_through_ordered_by_size()
    {
        var data = new AnalyticsData
        {
            OpenInvestigations =
            [
                new MappingGap(InvestigationKind.DuplicateStripeCustomer, 8),
                new MappingGap(InvestigationKind.UnmappedIdentity, 45),
            ],
        };

        var s = AnalyticsRules.Compute(data, Now);

        s.MappingGaps[0].Kind.Should().Be(InvestigationKind.UnmappedIdentity, "biggest gap surfaces first");
        s.TotalOpenGaps.Should().Be(53);
    }
}
