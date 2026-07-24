using RD.Domain;

namespace RD.Web.Services;

/// <summary>
/// Pure owner-analytics derivation — no DB, no clock, no I/O. Takes loaded
/// facts plus "now" and produces the snapshot the /analytics page renders.
/// Unit-tested directly, exactly like <see cref="CockpitRules"/>.
/// </summary>
public static class AnalyticsRules
{
    /// <summary>Enforcement-activity time series length (design doc M3: last 14–30 days).</summary>
    public const int DefaultWindowDays = 30;

    /// <summary>Bucket for revenue from clients with no package assigned — honest, not invented.</summary>
    public const string UnassignedPackage = "Unassigned";

    /// <summary>Canonical action order so the chart's series + colors stay stable across renders.</summary>
    private static readonly ProposedActionType[] ActionOrder =
    [
        ProposedActionType.None, ProposedActionType.Pause, ProposedActionType.Resume,
        ProposedActionType.DunningStep, ProposedActionType.Escalate, ProposedActionType.Investigate,
    ];

    public static AnalyticsSnapshot Compute(AnalyticsData data, DateTimeOffset now)
        => Compute(data, now, DefaultWindowDays);

    public static AnalyticsSnapshot Compute(AnalyticsData data, DateTimeOffset now, int windowDays)
    {
        // ---- Per-client money: ChargePaid positive, AdSpend cost = −SignedAmount (stored negative).
        var byClient = data.MasterClientLedger
            .GroupBy(f => f.ClientId)
            .ToDictionary(
                g => g.Key,
                g => (
                    Paid: g.Where(f => f.Type == LedgerEntryType.ChargePaid).Sum(f => f.SignedAmount),
                    AdSpendCost: g.Where(f => f.Type == LedgerEntryType.AdSpend).Sum(f => -f.SignedAmount)));

        // ---- Net cash position per master client (signed), most-negative first.
        var positions = data.MasterClients
            .Select(c =>
            {
                var money = byClient.GetValueOrDefault(c.ClientId);
                var paid = money.Paid;
                var cost = money.AdSpendCost;
                return new ClientNetPosition(
                    c.ClientId, c.BusinessName, c.CurrencyCode, c.Mode,
                    paid, cost, paid - cost, Math.Max(0m, cost - paid));
            })
            // Only clients that actually moved money — a page full of $0 rows is noise.
            .Where(p => p.Paid != 0m || p.AdSpendCost != 0m)
            .OrderBy(p => p.NetPosition)
            .ThenByDescending(p => p.AdSpendCost)
            .ToList();

        // ---- Company roll-up per currency (USD leads, always present; no FX).
        var rollups = positions
            .GroupBy(p => p.CurrencyCode)
            .Select(g => new CurrencyRollup(
                g.Key,
                NetPosition: g.Sum(p => p.NetPosition),
                Paid: g.Sum(p => p.Paid),
                AdSpendCost: g.Sum(p => p.AdSpendCost),
                Exposure: g.Sum(p => p.Exposure),
                ClientCount: g.Count(),
                NegativeClientCount: g.Count(p => p.NetPosition < 0)))
            .ToList();
        if (rollups.All(r => r.CurrencyCode != "USD"))
            rollups.Add(new CurrencyRollup("USD", 0m, 0m, 0m, 0m, 0, 0));
        var rollupsOrdered = OrderUsdFirst(rollups, r => r.CurrencyCode);

        // ---- Company exposure per currency (Σ per-client max(0, spend − paid)) — same shape as the cockpit.
        var exposure = positions
            .GroupBy(p => p.CurrencyCode)
            .Select(g => new CurrencyExposure(g.Key, g.Sum(p => p.Exposure), g.Count(p => p.Exposure > 0)))
            .ToList();
        if (exposure.All(e => e.CurrencyCode != "USD"))
            exposure.Add(new CurrencyExposure("USD", 0m, 0));
        var exposureOrdered = OrderUsdFirst(exposure, e => e.CurrencyCode);

        // ---- Package / offer revenue mix: ChargePaid by the client's package, Unassigned bucket, per currency.
        var packageRevenue = data.MasterClients
            .Select(c => new { Package = c.PackageName ?? UnassignedPackage, c.CurrencyCode, Paid = byClient.GetValueOrDefault(c.ClientId).Paid })
            .Where(x => x.Paid > 0m)
            .GroupBy(x => new { x.Package, x.CurrencyCode })
            .Select(g => new PackageRevenueSlice(g.Key.Package, g.Key.CurrencyCode, g.Sum(x => x.Paid), g.Count()))
            .OrderByDescending(s => s.Paid)
            .ToList();

        // ---- Enforcement activity over the last N days (a per-action time series).
        var today = DateOnly.FromDateTime(now.UtcDateTime.Date);
        var span = Math.Max(1, windowDays);
        var days = Enumerable.Range(0, span)
            .Select(i => today.AddDays(-(span - 1 - i)))
            .ToList();
        var dayIndex = days.Select((d, i) => (d, i)).ToDictionary(x => x.d, x => x.i);

        var series = ActionOrder
            .Where(a => data.DecisionsByDay.Any(f => f.Action == a))
            .Select(a =>
            {
                var counts = new int[days.Count];
                foreach (var f in data.DecisionsByDay)
                    if (f.Action == a && dayIndex.TryGetValue(f.Day, out var idx))
                        counts[idx] += f.Count;
                return new ActionDaySeries(a, counts);
            })
            .ToList();
        var activity = new EnforcementActivity(days, series);

        // ---- Reconciliation health + client mix (pass-through, ordered for display).
        var gaps = data.OpenInvestigations.OrderByDescending(g => g.Count).ToList();
        var segments = data.ClientSegments
            .Select(s => new ClientSegmentCount(s.AccountType, s.ContractType, s.Count))
            .OrderBy(s => s.AccountType).ThenBy(s => s.ContractType)
            .ToList();
        var modes = Enum.GetValues<EnforcementMode>()
            .Select(m => new ModeCount(m, data.MasterModes.Where(x => x.Mode == m).Sum(x => x.Count)))
            .ToList();

        return new AnalyticsSnapshot
        {
            ComputedAt = now,
            NetPositionByCurrency = rollupsOrdered,
            ClientNetPositions = positions,
            ExposureByCurrency = exposureOrdered,
            PackageRevenue = packageRevenue,
            EnforcementActivity = activity,
            MappingGaps = gaps,
            MasterClientCount = data.MasterClientCount,
            VerifiedMasterClientCount = data.VerifiedMasterClientCount,
            ClientSegments = segments,
            ModeMix = modes,
        };
    }

    /// <summary>USD always sorts first; the rest alphabetically. Matches the cockpit's currency ordering.</summary>
    private static List<T> OrderUsdFirst<T>(IEnumerable<T> items, Func<T, string> currency) => items
        .OrderBy(x => currency(x) == "USD" ? 0 : 1)
        .ThenBy(currency, StringComparer.Ordinal)
        .ToList();
}
