using RD.Domain;

namespace RD.Web.Services;

/// <summary>
/// Pure cockpit derivation — no DB, no clock, no I/O. Takes loaded data plus
/// "now" and produces the snapshot the page renders. Unit-tested directly.
/// </summary>
public static class CockpitRules
{
    /// <summary>Default freshness bound (design doc: staleness bound, default 30 min).</summary>
    public static readonly TimeSpan StalenessBound = TimeSpan.FromMinutes(30);

    /// <summary>Only Stripe and Meta gate freshness — they feed money decisions.</summary>
    private static readonly ExternalSystem[] FreshnessGated = [ExternalSystem.Stripe, ExternalSystem.Meta];

    private static readonly ExternalSystem[] AllSystems =
        [ExternalSystem.Stripe, ExternalSystem.Meta, ExternalSystem.Ghl, ExternalSystem.ClickUp];

    public static CockpitSnapshot Compute(CockpitData data, DateTimeOffset now)
        => Compute(data, now, StalenessBound);

    public static CockpitSnapshot Compute(CockpitData data, DateTimeOffset now, TimeSpan stalenessBound)
    {
        // ---- Exposure v1: per master client, max(0, ad spend − charges paid), bucketed by the client's currency. No FX.
        var perClient = data.MasterClientLedger
            .GroupBy(f => f.ClientId)
            .Select(g =>
            {
                var adSpend = g.Where(f => f.Type == LedgerEntryType.AdSpend).Sum(f => -f.SignedAmount);
                var paid = g.Where(f => f.Type == LedgerEntryType.ChargePaid).Sum(f => f.SignedAmount);
                var lastBilling = g
                    .Where(f => f.Type is LedgerEntryType.ChargePaid or LedgerEntryType.ChargeFailed)
                    .OrderByDescending(f => f.OccurredAt)
                    .FirstOrDefault();
                return new
                {
                    Currency = g.First().ClientCurrency,
                    Exposure = Math.Max(0m, adSpend - paid),
                    LastBillingFailed = lastBilling?.Type == LedgerEntryType.ChargeFailed,
                };
            })
            .ToList();

        var exposure = perClient
            .GroupBy(c => c.Currency)
            .Select(g => new CurrencyExposure(g.Key, g.Sum(c => c.Exposure), g.Count(c => c.Exposure > 0)))
            .ToList();
        if (exposure.All(e => e.CurrencyCode != "USD"))
            exposure.Add(new CurrencyExposure("USD", 0m, 0));
        var exposureOrdered = exposure
            .OrderBy(e => e.CurrencyCode == "USD" ? 0 : 1)
            .ThenBy(e => e.CurrencyCode, StringComparer.Ordinal)
            .ToList();

        var clientsAtRisk = perClient.Count(c => c.LastBillingFailed);

        var todayUtc = now.UtcDateTime.Date;
        var failedToday = data.MasterClientLedger
            .Count(f => f.Type == LedgerEntryType.ChargeFailed && f.OccurredAt.UtcDateTime.Date == todayUtc);

        // ---- Sync freshness per system; staleness only judged on the gated systems.
        var latestBySystem = data.CompletedSyncRuns
            .GroupBy(s => s.System)
            .ToDictionary(g => g.Key, g => (DateTimeOffset?)g.Max(s => s.CompletedAt));
        var freshness = AllSystems
            .Select(sys =>
            {
                var last = latestBySystem.GetValueOrDefault(sys);
                var gated = FreshnessGated.Contains(sys);
                var stale = gated && (last is null || now - last.Value > stalenessBound);
                return new SystemFreshness(sys, last, stale);
            })
            .ToList();

        // ---- State ladder: FirstRun beats Stale beats AllQuiet/Busy.
        var state =
            data.CompletedSyncRuns.Count == 0 ? CockpitState.FirstRun
            : freshness.Any(f => f.IsStale) ? CockpitState.Stale
            : data.OpenActionCount + data.ShadowVerdictCount == 0 ? CockpitState.AllQuiet
            : CockpitState.Busy;

        return new CockpitSnapshot
        {
            State = state,
            ComputedAt = now,
            ExposureByCurrency = exposureOrdered,
            ClientsAtRisk = clientsAtRisk,
            CampaignsLive = data.CampaignsLiveMasterClients,
            MasterClientCount = data.MasterClientCount,
            FailedPaymentsToday = failedToday,
            OpenActionCount = data.OpenActionCount,
            ShadowVerdictCount = data.ShadowVerdictCount,
            MappingGaps = data.OpenInvestigations.OrderByDescending(g => g.Count).ToList(),
            SyncFreshness = freshness,
            ClientsLinked = data.ClientsWithActiveStripeSubscription,
            TotalClients = data.TotalClients,
        };
    }
}
