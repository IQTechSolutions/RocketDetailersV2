using FluentAssertions;
using RD.Domain;
using RD.Domain.Policy;

namespace RD.Tests;

/// <summary>
/// Golden coverage of the state→action table. Every rule of
/// EligibilityPolicy's precedence ladder has at least one test; production
/// Decision.StateSnapshotJson rows replay through the same entry point.
/// </summary>
public class EligibilityPolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 07, 24, 12, 0, 0, TimeSpan.Zero);

    private static ClientState Base(EnforcementMode mode = EnforcementMode.Shadow) => new()
    {
        ClientId = Guid.NewGuid(),
        Mode = mode,
        Contract = ContractType.Paid,
        Account = AccountType.Master,
        CurrencyCode = "USD",
        MappingVerified = true,
        SubscriptionStatus = "active",
        StripeSyncedAt = T0.AddMinutes(-5),
        MetaSyncedAt = T0.AddMinutes(-5),
        EvaluatedAt = T0,
        Campaigns = [new("c1", "ACTIVE", false)],
    };

    private static DunningState OpenDunning(
        int lastStep = 1,
        DateTimeOffset? windowExpiresAt = null,
        DateTimeOffset? nextStepDueAt = null,
        bool allVerified = true,
        DateTimeOffset? oldestUnverifiedSince = null) =>
        new(lastStep, windowExpiresAt ?? T0.AddHours(12), nextStepDueAt, allVerified, oldestUnverifiedSince);

    // ── 1. Currency guard beats everything ─────────────────────────────────

    [Fact]
    public void NonUsd_client_is_investigated_even_at_final_failure()
    {
        var s = Base() with
        {
            CurrencyCode = "CAD",
            Dunning = OpenDunning(lastStep: 3, windowExpiresAt: T0.AddHours(-1)),
        };
        var d = EligibilityPolicy.Evaluate(s);
        d.Action.Should().Be(ProposedActionType.Investigate);
        d.Investigation.Should().Be(InvestigationKind.NonUsdCurrency);
    }

    // ── 2. Own-account clients never get campaign actions ──────────────────

    [Fact]
    public void Own_account_gets_no_action_even_when_unpaid()
    {
        var s = Base() with { Account = AccountType.Own, SubscriptionStatus = "unpaid" };
        EligibilityPolicy.Evaluate(s).Action.Should().Be(ProposedActionType.None);
    }

    // ── 3. Unmapped identity blocks enforcement and demotes ────────────────

    [Fact]
    public void Unmapped_in_shadow_investigates_without_demotion()
    {
        var d = EligibilityPolicy.Evaluate(Base() with { MappingVerified = false });
        d.Action.Should().Be(ProposedActionType.Investigate);
        d.Investigation.Should().Be(InvestigationKind.UnmappedIdentity);
        d.DemoteToShadow.Should().BeFalse();
    }

    [Theory]
    [InlineData(EnforcementMode.Assist)]
    [InlineData(EnforcementMode.Auto)]
    public void Unmapped_in_assist_or_auto_demotes_to_shadow(EnforcementMode mode)
    {
        var d = EligibilityPolicy.Evaluate(Base(mode) with { MappingVerified = false });
        d.DemoteToShadow.Should().BeTrue();
    }

    [Fact]
    public void Unmapped_beats_final_failure_pause()
    {
        var s = Base(EnforcementMode.Auto) with
        {
            MappingVerified = false,
            Dunning = OpenDunning(lastStep: 3, windowExpiresAt: T0.AddHours(-1)),
        };
        EligibilityPolicy.Evaluate(s).Investigation.Should().Be(InvestigationKind.UnmappedIdentity);
    }

    // ── 4. Trials suppress enforcement only when bounded ───────────────────

    [Fact]
    public void Active_bounded_trial_suppresses_enforcement()
    {
        var s = Base() with
        {
            Contract = ContractType.Trial,
            HasActiveTrial = true,
            TrialExpiresAt = T0.AddDays(3),
            SubscriptionStatus = "past_due",
            HasNewFailedCharge = true,
        };
        EligibilityPolicy.Evaluate(s).Action.Should().Be(ProposedActionType.None);
    }

    [Fact]
    public void Trial_without_expiry_is_investigated_not_exempt()
    {
        var s = Base() with { HasActiveTrial = true, TrialExpiresAt = null };
        var d = EligibilityPolicy.Evaluate(s);
        d.Action.Should().Be(ProposedActionType.Investigate);
        d.Investigation.Should().Be(InvestigationKind.MissingTrialExpiry);
    }

    [Fact]
    public void Trial_over_spend_cap_escalates()
    {
        var s = Base() with
        {
            HasActiveTrial = true,
            TrialExpiresAt = T0.AddDays(3),
            TrialSpend = 120m,
            TrialSpendCap = 100m,
        };
        var d = EligibilityPolicy.Evaluate(s);
        d.Action.Should().Be(ProposedActionType.Escalate);
        d.Investigation.Should().Be(InvestigationKind.ExposureCapExceeded);
    }

    [Fact]
    public void Expired_trial_falls_through_to_billing_enforcement()
    {
        var s = Base() with
        {
            HasActiveTrial = true,
            TrialExpiresAt = T0.AddDays(-1),
            SubscriptionStatus = "past_due",
        };
        var d = EligibilityPolicy.Evaluate(s);
        d.Action.Should().Be(ProposedActionType.DunningStep);
        d.DunningStep.Should().Be(1);
    }

    // ── 5. Canceled-sub payment edge ───────────────────────────────────────

    [Fact]
    public void Payment_on_canceled_sub_goes_to_resubscribe_flow()
    {
        var s = Base() with { SubscriptionStatus = "canceled", PaymentReceivedForCanceledSub = true };
        var d = EligibilityPolicy.Evaluate(s);
        d.Action.Should().Be(ProposedActionType.Investigate);
        d.Investigation.Should().Be(InvestigationKind.CanceledSubPayment);
    }

    // ── 6. Max-loss cap ────────────────────────────────────────────────────

    [Fact]
    public void Exposure_over_cap_escalates_to_human()
    {
        var s = Base() with { Exposure = 250m, MaxLossCap = 200m };
        var d = EligibilityPolicy.Evaluate(s);
        d.Action.Should().Be(ProposedActionType.Escalate);
        d.Investigation.Should().Be(InvestigationKind.ExposureCapExceeded);
    }

    // ── 7. The dunning ladder and the pause trigger ────────────────────────

    [Fact]
    public void Final_failure_with_verified_warnings_pauses_active_campaigns()
    {
        var s = Base() with
        {
            Dunning = OpenDunning(lastStep: 4, windowExpiresAt: T0.AddMinutes(-10)),
            Campaigns = [new("c1", "ACTIVE", false), new("c2", "PAUSED", false)],
        };
        var d = EligibilityPolicy.Evaluate(s);
        d.Action.Should().Be(ProposedActionType.Pause);
        d.TargetCampaignIds.Should().Equal("c1"); // only the running one
    }

    [Fact]
    public void Window_expiry_with_unverified_warnings_never_pauses()
    {
        var s = Base() with
        {
            Dunning = OpenDunning(lastStep: 4, windowExpiresAt: T0.AddMinutes(-10), allVerified: false),
        };
        var d = EligibilityPolicy.Evaluate(s);
        d.Action.Should().Be(ProposedActionType.Escalate);
        d.Investigation.Should().Be(InvestigationKind.DeliveryUnverified);
    }

    [Fact]
    public void Zero_verified_sends_never_pauses_even_when_window_expired()
    {
        var s = Base() with
        {
            Dunning = OpenDunning(lastStep: 0, windowExpiresAt: T0.AddMinutes(-10), allVerified: true),
        };
        EligibilityPolicy.Evaluate(s).Action.Should().Be(ProposedActionType.Escalate);
    }

    [Fact]
    public void Stalled_unverified_sends_escalate_before_window_expiry()
    {
        var s = Base() with
        {
            Dunning = OpenDunning(oldestUnverifiedSince: T0.AddHours(-7), allVerified: false),
        };
        var d = EligibilityPolicy.Evaluate(s);
        d.Action.Should().Be(ProposedActionType.Escalate);
        d.Investigation.Should().Be(InvestigationKind.DeliveryUnverified);
    }

    [Fact]
    public void Due_step_proposes_next_dunning_step()
    {
        var s = Base() with { Dunning = OpenDunning(lastStep: 2, nextStepDueAt: T0.AddMinutes(-1)) };
        var d = EligibilityPolicy.Evaluate(s);
        d.Action.Should().Be(ProposedActionType.DunningStep);
        d.DunningStep.Should().Be(3);
    }

    [Fact]
    public void Between_steps_the_engine_waits()
    {
        var s = Base() with { Dunning = OpenDunning(lastStep: 2, nextStepDueAt: T0.AddHours(2)) };
        EligibilityPolicy.Evaluate(s).Action.Should().Be(ProposedActionType.None);
    }

    // ── 8/9. Entry points into enforcement ─────────────────────────────────

    [Theory]
    [InlineData("unpaid")]
    [InlineData("canceled")]
    public void Dead_subscription_without_case_proposes_pause(string status)
    {
        var s = Base() with { SubscriptionStatus = status };
        EligibilityPolicy.Evaluate(s).Action.Should().Be(ProposedActionType.Pause);
    }

    [Theory]
    [InlineData("active", true, 0)]
    [InlineData("past_due", false, 0)]
    [InlineData("active", false, 2)]
    public void Fresh_failure_opens_dunning_at_step_one(string subStatus, bool newFailedCharge, int openInvoices)
    {
        var s = Base() with
        {
            SubscriptionStatus = subStatus,
            HasNewFailedCharge = newFailedCharge,
            OpenUnpaidInvoices = openInvoices,
        };
        var d = EligibilityPolicy.Evaluate(s);
        d.Action.Should().Be(ProposedActionType.DunningStep);
        d.DunningStep.Should().Be(1);
    }

    // ── 10. Paid up: provenance-respecting resume ──────────────────────────

    [Fact]
    public void Paid_up_resumes_only_app_paused_campaigns()
    {
        var s = Base() with
        {
            Campaigns =
            [
                new("app-paused", "PAUSED", PausedByApp: true),
                new("human-paused", "PAUSED", PausedByApp: false),
                new("running", "ACTIVE", PausedByApp: false),
            ],
        };
        var d = EligibilityPolicy.Evaluate(s);
        d.Action.Should().Be(ProposedActionType.Resume);
        d.TargetCampaignIds.Should().Equal("app-paused");
    }

    [Fact]
    public void Paid_up_with_only_external_pause_is_investigated_never_resumed()
    {
        var s = Base() with { Campaigns = [new("c1", "PAUSED", PausedByApp: false)] };
        var d = EligibilityPolicy.Evaluate(s);
        d.Action.Should().Be(ProposedActionType.Investigate);
        d.Investigation.Should().Be(InvestigationKind.ExternallyPausedPayment);
    }

    [Fact]
    public void Paid_up_and_running_needs_nothing()
    {
        EligibilityPolicy.Evaluate(Base()).Action.Should().Be(ProposedActionType.None);
    }

    // ── Freshness gate ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(EnforcementMode.Assist)]
    [InlineData(EnforcementMode.Auto)]
    public void Stale_stripe_downgrades_enforcement_in_assist_and_auto(EnforcementMode mode)
    {
        var s = Base(mode) with
        {
            StripeSyncedAt = T0.AddHours(-2),
            Dunning = OpenDunning(lastStep: 4, windowExpiresAt: T0.AddMinutes(-10)),
        };
        var d = EligibilityPolicy.Evaluate(s);
        d.Action.Should().Be(ProposedActionType.Investigate);
        d.Investigation.Should().Be(InvestigationKind.StaleSync);
    }

    [Fact]
    public void Shadow_mode_still_logs_the_pause_on_stale_data()
    {
        var s = Base(EnforcementMode.Shadow) with
        {
            StripeSyncedAt = T0.AddHours(-2),
            Dunning = OpenDunning(lastStep: 4, windowExpiresAt: T0.AddMinutes(-10)),
        };
        EligibilityPolicy.Evaluate(s).Action.Should().Be(ProposedActionType.Pause);
    }

    [Fact]
    public void Stale_meta_blocks_resume_too()
    {
        var s = Base(EnforcementMode.Auto) with
        {
            MetaSyncedAt = null,
            Campaigns = [new("c1", "PAUSED", PausedByApp: true)],
        };
        var d = EligibilityPolicy.Evaluate(s);
        d.Action.Should().Be(ProposedActionType.Investigate);
        d.Investigation.Should().Be(InvestigationKind.StaleSync);
    }

    // ── Snapshot round-trip: decisions must be replayable ──────────────────

    [Fact]
    public void ClientState_survives_json_round_trip_with_identical_verdict()
    {
        var s = Base(EnforcementMode.Auto) with
        {
            Dunning = OpenDunning(lastStep: 2, nextStepDueAt: T0.AddMinutes(-1)),
        };
        var json = System.Text.Json.JsonSerializer.Serialize(s);
        var replayed = System.Text.Json.JsonSerializer.Deserialize<ClientState>(json)!;
        EligibilityPolicy.Evaluate(replayed).Should().BeEquivalentTo(EligibilityPolicy.Evaluate(s));
    }

    // ── Step 3: arrangement balance (payments must cover master-account ad spend) ──

    private static ClientState WithArrangement(decimal paid, decimal adSpend, decimal expected,
        ArrangementStatus status = ArrangementStatus.Inferred) => Base() with
    {
        TotalPaid = paid,
        TotalAdSpend = adSpend,
        ArrangementAmount = expected,
        ArrangementStatus = status,
    };

    [Fact]
    public void Behind_more_than_one_payment_pauses()
    {
        // paid 100 vs ad spend 500 → balance −400, more than one 200 payment short.
        var d = EligibilityPolicy.Evaluate(WithArrangement(paid: 100m, adSpend: 500m, expected: 200m));
        d.Action.Should().Be(ProposedActionType.Pause);
        d.Reason.Should().Contain("Behind on arrangement");
        d.TargetCampaignIds.Should().Contain("c1");
    }

    [Fact]
    public void In_the_green_does_not_pause_on_the_balance_rule()
    {
        // paid 500 vs ad spend 400 → balance +100 → falls through to normal handling.
        var d = EligibilityPolicy.Evaluate(WithArrangement(paid: 500m, adSpend: 400m, expected: 200m));
        d.Action.Should().NotBe(ProposedActionType.Pause);
    }

    [Fact]
    public void Behind_by_less_than_one_payment_is_within_grace()
    {
        // paid 400 vs ad spend 500 → balance −100, not past the 200 grace.
        var d = EligibilityPolicy.Evaluate(WithArrangement(paid: 400m, adSpend: 500m, expected: 200m));
        d.Action.Should().NotBe(ProposedActionType.Pause);
    }

    [Fact]
    public void Unknown_arrangement_never_triggers_the_balance_rule()
    {
        // Deeply underwater, but no established arrangement → defer to Stripe/dunning signals.
        var d = EligibilityPolicy.Evaluate(WithArrangement(paid: 0m, adSpend: 900m, expected: 200m,
            status: ArrangementStatus.NeedsReview));
        d.Reason.Should().NotContain("Behind on arrangement");
    }
}
