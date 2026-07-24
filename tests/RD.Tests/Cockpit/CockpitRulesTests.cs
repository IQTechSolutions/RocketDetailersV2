using FluentAssertions;
using RD.Domain;
using RD.Web.Services;

namespace RD.Tests.Cockpit;

/// <summary>
/// Pure-logic tests for the cockpit state derivation (F6 interaction states)
/// and the v1 exposure math. No DB, no rendering — constructed data only.
/// </summary>
public class CockpitRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 07, 24, 12, 00, 00, TimeSpan.Zero);

    private static CompletedSyncFact Sync(ExternalSystem system, int minutesAgo)
        => new(system, Now.AddMinutes(-minutesAgo));

    private static LedgerFact Fact(Guid clientId, LedgerEntryType type, decimal signedAmount,
        string currency = "USD", DateTimeOffset? occurredAt = null)
        => new(clientId, currency, type, signedAmount, occurredAt ?? Now.AddHours(-1));

    // ---------------- State derivation ----------------

    [Fact]
    public void FirstRun_when_no_completed_sync_runs_at_all()
    {
        var data = new CockpitData { OpenActionCount = 5 };

        var snapshot = CockpitRules.Compute(data, Now);

        snapshot.State.Should().Be(CockpitState.FirstRun);
    }

    [Fact]
    public void Stale_when_stripe_sync_older_than_30_minutes()
    {
        var data = new CockpitData
        {
            CompletedSyncRuns = [Sync(ExternalSystem.Stripe, 47), Sync(ExternalSystem.Meta, 5)],
        };

        var snapshot = CockpitRules.Compute(data, Now);

        snapshot.State.Should().Be(CockpitState.Stale);
        snapshot.StaleSystems.Should().ContainSingle(f => f.System == ExternalSystem.Stripe);
    }

    [Fact]
    public void Stale_when_meta_never_completed_but_other_syncs_exist()
    {
        // Not FirstRun (a completed run exists), but Meta has never completed → its data is stale by definition.
        var data = new CockpitData
        {
            CompletedSyncRuns = [Sync(ExternalSystem.Stripe, 5)],
        };

        var snapshot = CockpitRules.Compute(data, Now);

        snapshot.State.Should().Be(CockpitState.Stale);
        snapshot.StaleSystems.Should().ContainSingle(f => f.System == ExternalSystem.Meta && f.LastCompletedAt == null);
    }

    [Fact]
    public void AllQuiet_when_fresh_and_no_open_actions()
    {
        var data = new CockpitData
        {
            CompletedSyncRuns = [Sync(ExternalSystem.Stripe, 10), Sync(ExternalSystem.Meta, 12)],
            OpenActionCount = 0,
        };

        var snapshot = CockpitRules.Compute(data, Now);

        snapshot.State.Should().Be(CockpitState.AllQuiet);
    }

    [Fact]
    public void Busy_when_fresh_with_open_actions()
    {
        var data = new CockpitData
        {
            CompletedSyncRuns = [Sync(ExternalSystem.Stripe, 10), Sync(ExternalSystem.Meta, 12)],
            OpenActionCount = 3,
        };

        var snapshot = CockpitRules.Compute(data, Now);

        snapshot.State.Should().Be(CockpitState.Busy);
    }

    [Fact]
    public void Uses_newest_completed_run_per_system_for_staleness()
    {
        // An old Stripe run plus a fresh one → fresh wins.
        var data = new CockpitData
        {
            CompletedSyncRuns =
            [
                Sync(ExternalSystem.Stripe, 300), Sync(ExternalSystem.Stripe, 3),
                Sync(ExternalSystem.Meta, 8),
            ],
        };

        CockpitRules.Compute(data, Now).State.Should().Be(CockpitState.AllQuiet);
    }

    [Fact]
    public void Ghl_and_clickup_staleness_never_gates_the_cockpit()
    {
        // Only Stripe and Meta feed money decisions; other systems don't trip the banner.
        var data = new CockpitData
        {
            CompletedSyncRuns =
            [
                Sync(ExternalSystem.Stripe, 5), Sync(ExternalSystem.Meta, 5),
                Sync(ExternalSystem.Ghl, 500),
            ],
        };

        CockpitRules.Compute(data, Now).State.Should().Be(CockpitState.AllQuiet);
    }

    // ---------------- Exposure v1 ----------------

    [Fact]
    public void Exposure_is_ad_spend_minus_paid_floored_at_zero_per_client()
    {
        var underwater = Guid.NewGuid(); // spent 100, paid 40 → 60 exposed
        var overpaid = Guid.NewGuid();   // spent 10, paid 50 → 0 (never negative)

        var data = new CockpitData
        {
            MasterClientLedger =
            [
                Fact(underwater, LedgerEntryType.AdSpend, -100m),
                Fact(underwater, LedgerEntryType.ChargePaid, 40m),
                Fact(overpaid, LedgerEntryType.AdSpend, -10m),
                Fact(overpaid, LedgerEntryType.ChargePaid, 50m),
            ],
        };

        var snapshot = CockpitRules.Compute(data, Now);

        var usd = snapshot.ExposureByCurrency.Single(e => e.CurrencyCode == "USD");
        usd.Amount.Should().Be(60m);
        usd.ClientCount.Should().Be(1, "only the underwater client has exposure");
    }

    [Fact]
    public void Non_usd_clients_get_their_own_currency_bucket()
    {
        var cad = Guid.NewGuid();
        var data = new CockpitData
        {
            MasterClientLedger =
            [
                Fact(cad, LedgerEntryType.AdSpend, -30m, currency: "CAD"),
            ],
        };

        var snapshot = CockpitRules.Compute(data, Now);

        snapshot.ExposureByCurrency.Should().HaveCount(2);
        snapshot.ExposureByCurrency[0].CurrencyCode.Should().Be("USD", "USD leads even when zero");
        snapshot.ExposureByCurrency.Single(e => e.CurrencyCode == "CAD").Amount.Should().Be(30m);
    }

    [Fact]
    public void Empty_ledger_still_reports_a_zero_usd_row()
    {
        var snapshot = CockpitRules.Compute(new CockpitData(), Now);

        snapshot.UsdExposure.Should().Be(0m);
        snapshot.ExposureByCurrency.Should().ContainSingle(e => e.CurrencyCode == "USD");
    }

    // ---------------- Clients at risk & failed-today ----------------

    [Fact]
    public void Client_at_risk_when_latest_billing_event_is_a_failure()
    {
        var atRisk = Guid.NewGuid();
        var recovered = Guid.NewGuid();

        var data = new CockpitData
        {
            MasterClientLedger =
            [
                Fact(atRisk, LedgerEntryType.ChargePaid, 25m, occurredAt: Now.AddDays(-2)),
                Fact(atRisk, LedgerEntryType.ChargeFailed, 0m, occurredAt: Now.AddHours(-3)),
                Fact(recovered, LedgerEntryType.ChargeFailed, 0m, occurredAt: Now.AddDays(-1)),
                Fact(recovered, LedgerEntryType.ChargePaid, 25m, occurredAt: Now.AddHours(-1)),
            ],
        };

        CockpitRules.Compute(data, Now).ClientsAtRisk.Should().Be(1);
    }

    [Fact]
    public void Failed_payments_today_counts_only_todays_utc_failures()
    {
        var client = Guid.NewGuid();
        var data = new CockpitData
        {
            MasterClientLedger =
            [
                Fact(client, LedgerEntryType.ChargeFailed, 0m, occurredAt: Now.AddHours(-2)),
                Fact(client, LedgerEntryType.ChargeFailed, 0m, occurredAt: Now.AddDays(-1)),
            ],
        };

        CockpitRules.Compute(data, Now).FailedPaymentsToday.Should().Be(1);
    }

    // ---------------- Pass-through counts ----------------

    [Fact]
    public void Snapshot_carries_linked_counts_and_gaps_ordered_by_size()
    {
        var data = new CockpitData
        {
            TotalClients = 646,
            MasterClientCount = 177,
            ClientsWithActiveStripeSubscription = 268,
            OpenInvestigations =
            [
                new MappingGap(InvestigationKind.DuplicateStripeCustomer, 8),
                new MappingGap(InvestigationKind.UnmappedIdentity, 45),
            ],
        };

        var snapshot = CockpitRules.Compute(data, Now);

        snapshot.ClientsLinked.Should().Be(268);
        snapshot.TotalClients.Should().Be(646);
        snapshot.MappingGaps[0].Kind.Should().Be(InvestigationKind.UnmappedIdentity, "biggest gap surfaces first");
    }
}
