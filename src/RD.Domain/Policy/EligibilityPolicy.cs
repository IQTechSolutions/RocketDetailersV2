namespace RD.Domain.Policy;

/// <summary>
/// The product: ads run only while the client is economically eligible.
/// Pure function — no I/O, no clock reads (time comes in via state), fully
/// replayable from a Decision.StateSnapshotJson.
///
/// Precedence (first match wins):
///
///   1  non-USD currency            ─▶ Investigate(NonUsdCurrency)
///   2  own-account client          ─▶ None (log-only; never campaign actions)
///   3  unmapped identity           ─▶ Investigate(UnmappedIdentity) [+ demote if Assist/Auto]
///   4  trial, genuinely active     ─▶ missing expiry → Investigate(MissingTrialExpiry)
///                                     cap exceeded  → Escalate(ExposureCapExceeded)
///                                     otherwise     → None (enforcement suppressed)
///   5  payment on canceled sub     ─▶ Investigate(CanceledSubPayment) (re-subscribe flow)
///   6  exposure over max-loss cap  ─▶ Escalate(ExposureCapExceeded)
///   7  open dunning case:
///        sends unverified > 6h     ─▶ Escalate(DeliveryUnverified)
///        window up + verified      ─▶ Pause  (FINAL FAILURE — the one that matters)
///        window up + unverified    ─▶ Escalate(DeliveryUnverified) (never pause off phantom warnings)
///        next step due             ─▶ DunningStep(n)
///        waiting between steps     ─▶ None
///   8  sub unpaid/canceled, no case─▶ Pause (routes through Assist even in Auto — see dispatcher)
///   9  new failed charge, no case  ─▶ DunningStep(1) (opens the case)
///  10  current (paid up):
///        app-paused campaigns      ─▶ Resume (ONLY app-paused entities)
///        externally paused + paid  ─▶ Investigate(ExternallyPausedPayment) (never auto-resume)
///        otherwise                 ─▶ None
///
/// Freshness gate: in Assist/Auto, enforcement writes (Pause / Resume /
/// DunningStep) additionally require both Stripe and Meta syncs within
/// StalenessBound; stale data downgrades the verdict to Investigate(StaleSync).
/// Shadow mode logs normally on stale data — observation is harmless.
/// </summary>
public static class EligibilityPolicy
{
    public const string Version = "1.0.0";
    public const string ExternallyPausedPaymentReason =
        "Client is paid up but a campaign was paused outside the app — never auto-resume someone else's pause.";

    public static readonly TimeSpan StalenessBound = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan UnverifiedSendEscalationAfter = TimeSpan.FromHours(6);

    public static PolicyDecision Evaluate(ClientState s)
    {
        // 1. Currency guard — v1 enforces USD only.
        if (!string.Equals(s.CurrencyCode, "USD", StringComparison.OrdinalIgnoreCase))
            return new(ProposedActionType.Investigate,
                $"Client bills in {s.CurrencyCode}; v1 policy acts on USD only.",
                InvestigationKind.NonUsdCurrency);

        // 2. Own-account clients create no ad-spend exposure — never campaign actions.
        if (s.Account == AccountType.Own)
            return new(ProposedActionType.None,
                "Own ad account — client pays Meta directly; service-fee lapses surface via the work queue.");

        // 3. Enforcement without verified mappings pauses the wrong client.
        if (!s.MappingVerified)
            return new(ProposedActionType.Investigate,
                "Identity mapping not verified — enforcement impossible until Stripe↔Meta↔GHL links are confirmed.",
                InvestigationKind.UnmappedIdentity,
                DemoteToShadow: s.Mode != EnforcementMode.Shadow);

        // 4. Trials suppress payment enforcement — but only bounded trials.
        if (s.HasActiveTrial)
        {
            if (s.TrialExpiresAt is null)
                return new(ProposedActionType.Investigate,
                    "Trial has no expiry date — unbounded free ads; backfill the real trial end.",
                    InvestigationKind.MissingTrialExpiry);
            if (s.TrialExpiresAt > s.EvaluatedAt)
            {
                if (s.TrialSpendCap is { } cap && s.TrialSpend > cap)
                    return new(ProposedActionType.Escalate,
                        $"Trial spend {s.TrialSpend:0.##} exceeded the package cap {cap:0.##}.",
                        InvestigationKind.ExposureCapExceeded);
                return new(ProposedActionType.None, "Trial active — payment enforcement suppressed until expiry.");
            }
            // Expired trial falls through to billing enforcement below.
        }

        // 5. A link payment cannot reactivate a canceled subscription.
        if (s.PaymentReceivedForCanceledSub)
            return new(ProposedActionType.Investigate,
                "Payment received but the subscription is canceled — needs the re-subscribe flow, not auto-resume.",
                InvestigationKind.CanceledSubPayment);

        // 6. Arrangement balance — the owner's core rule, a CLIENT-SPECIFIC threshold.
        // The client's payments must cover the master-account ad spend the agency
        // fronts; once they fall more than one expected payment behind
        // (paid − ad spend < −one period), pause. Fires only on an established
        // (inferred/confirmed) arrangement, and takes precedence over the generic
        // max-loss ceiling below — which backstops clients WITHOUT an arrangement.
        if (s.ArrangementStatus is ArrangementStatus.Inferred or ArrangementStatus.Confirmed
            && s.ArrangementAmount is { } expected && expected > 0m)
        {
            var balance = s.TotalPaid - s.TotalAdSpend;
            if (balance < -expected)
                return Gated(s, new(ProposedActionType.Pause,
                    $"Behind on arrangement — paid {s.TotalPaid:0.##} vs ad spend {s.TotalAdSpend:0.##} (balance {balance:0.##}), more than one {expected:0.##} payment short. Pause.",
                    TargetCampaignIds: ActiveCampaigns(s)));
        }

        // 7. Hard ceiling — generic backstop on unbilled spend (no arrangement, or
        // behind within one period's grace but still over the absolute cap).
        if (s.Exposure > s.MaxLossCap)
            return new(ProposedActionType.Escalate,
                $"Exposure {s.Exposure:0.##} exceeds the max-loss cap {s.MaxLossCap:0.##} — human decision required.",
                InvestigationKind.ExposureCapExceeded);

        // 8. Open dunning case — the Stripe-failure pause trigger lives here.
        if (s.Dunning is { } d)
        {
            if (d.OldestUnverifiedSince is { } u && s.EvaluatedAt - u > UnverifiedSendEscalationAfter)
                return new(ProposedActionType.Escalate,
                    "Dunning messages unverifiable for over 6h — GHL delivery may be broken; a client must never be final-failed off phantom warnings.",
                    InvestigationKind.DeliveryUnverified);

            if (d.WindowExpiresAt <= s.EvaluatedAt)
            {
                if (d.AllSendsVerified && d.LastStep > 0)
                    return Gated(s, new(ProposedActionType.Pause,
                        $"FINAL FAILURE: dunning window expired after {d.LastStep} verified warning(s) with no payment.",
                        TargetCampaignIds: ActiveCampaigns(s)));
                return new(ProposedActionType.Escalate,
                    "Dunning window expired but warnings were not all verified — escalating instead of pausing.",
                    InvestigationKind.DeliveryUnverified);
            }

            if (d.NextStepDueAt is { } due && due <= s.EvaluatedAt)
                return Gated(s, new(ProposedActionType.DunningStep,
                    $"Dunning step {d.LastStep + 1} due (window expires {d.WindowExpiresAt:u}).",
                    DunningStep: d.LastStep + 1));

            return new(ProposedActionType.None, "Dunning in progress — waiting for the next step or a payment.");
        }

        // 8. Stripe already gave up on this subscription.
        if (s.SubscriptionStatus is "unpaid" or "canceled")
            return Gated(s, new(ProposedActionType.Pause,
                $"Subscription is {s.SubscriptionStatus} — ads must not run unbilled. Routed through human approval.",
                TargetCampaignIds: ActiveCampaigns(s)));

        // 9. Fresh failure with no case yet — open dunning with step 1.
        if (s.HasNewFailedCharge || s.SubscriptionStatus == "past_due" || s.OpenUnpaidInvoices > 0)
            return Gated(s, new(ProposedActionType.DunningStep,
                "Failed/overdue charge with no open dunning case — starting the dunning window at step 1.",
                DunningStep: 1));

        // 10. Paid up.
        var appPaused = s.Campaigns.Where(c => c.IsPaused && c.PausedByApp).Select(c => c.CampaignId).ToList();
        if (appPaused.Count > 0)
            return Gated(s, new(ProposedActionType.Resume,
                "Paid up — resuming the campaigns the app itself paused (and only those).",
                TargetCampaignIds: appPaused));

        var externallyPaused = s.Campaigns.Any(c => c.IsPaused && !c.PausedByApp);
        if (externallyPaused)
            return new(ProposedActionType.Investigate,
                ExternallyPausedPaymentReason,
                InvestigationKind.ExternallyPausedPayment);

        return new(ProposedActionType.None, "Paid up, ads running — nothing to do.");
    }

    /// <summary>
    /// Freshness gate: Assist/Auto enforcement writes on stale vendor data
    /// downgrade to needs-investigation. A dead sync job must never cause a
    /// pause — and never a resume either (stricter than the design doc's
    /// letter, deliberately: resuming a nonpayer on stale data is also a loss).
    /// </summary>
    private static PolicyDecision Gated(ClientState s, PolicyDecision proposed)
    {
        if (s.Mode == EnforcementMode.Shadow) return proposed;
        var stripeStale = s.StripeSyncedAt is null || s.EvaluatedAt - s.StripeSyncedAt > StalenessBound;
        var metaStale = s.MetaSyncedAt is null || s.EvaluatedAt - s.MetaSyncedAt > StalenessBound;
        if (stripeStale || metaStale)
            return new(ProposedActionType.Investigate,
                $"Vendor data stale ({(stripeStale ? "Stripe" : "")}{(stripeStale && metaStale ? "+" : "")}{(metaStale ? "Meta" : "")}) — refusing to {proposed.Action} on old facts.",
                InvestigationKind.StaleSync);
        return proposed;
    }

    private static List<string> ActiveCampaigns(ClientState s) =>
        s.Campaigns.Where(c => c.IsActive).Select(c => c.CampaignId).ToList();
}
