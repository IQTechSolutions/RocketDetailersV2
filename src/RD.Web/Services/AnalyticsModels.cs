using RD.Domain;

namespace RD.Web.Services;

// ---------------------------------------------------------------------------
// Raw inputs for the pure analytics derivation — loaded from the DB by
// AnalyticsService, constructed by hand in tests. Mirrors CockpitModels.cs.
// (LedgerFact, MappingGap and CurrencyExposure are reused from CockpitModels.)
// ---------------------------------------------------------------------------

/// <summary>Per master-account client identity + segmentation metadata (no money).</summary>
public sealed record MasterClientFact(
    Guid ClientId, string BusinessName, string CurrencyCode, EnforcementMode Mode, string? PackageName);

/// <summary>One decisions count for a given UTC day + proposed action (grouped in SQL).</summary>
public sealed record DecisionDayFact(DateOnly Day, ProposedActionType Action, int Count);

/// <summary>Count of clients in one AccountType × ContractType segment (all clients).</summary>
public sealed record ClientSegmentFact(AccountType AccountType, ContractType ContractType, int Count);

/// <summary>Count of master-account clients on one rung of the enforcement ladder.</summary>
public sealed record ModeFact(EnforcementMode Mode, int Count);

/// <summary>Everything AnalyticsRules needs to derive the owner dashboard.</summary>
public sealed record AnalyticsData
{
    public int MasterClientCount { get; init; }
    /// <summary>Master clients with a current (non-invalidated) MappingVerification.</summary>
    public int VerifiedMasterClientCount { get; init; }
    public IReadOnlyList<MasterClientFact> MasterClients { get; init; } = [];
    /// <summary>ChargePaid + AdSpend ledger rows for master clients (currency = the client's), rolled up in memory.</summary>
    public IReadOnlyList<LedgerFact> MasterClientLedger { get; init; } = [];
    public IReadOnlyList<DecisionDayFact> DecisionsByDay { get; init; } = [];
    public IReadOnlyList<MappingGap> OpenInvestigations { get; init; } = [];
    public IReadOnlyList<ClientSegmentFact> ClientSegments { get; init; } = [];
    public IReadOnlyList<ModeFact> MasterModes { get; init; } = [];
}

// ---------------------------------------------------------------------------
// Derived snapshot the /analytics page renders, produced by AnalyticsRules.Compute.
// ---------------------------------------------------------------------------

/// <summary>
/// One master client's net cash position: cumulative charges paid minus
/// cumulative ad-spend cost, in the client's own currency (no FX).
/// NetPosition is signed; Exposure floors the underwater side at zero.
/// </summary>
public sealed record ClientNetPosition(
    Guid ClientId, string BusinessName, string CurrencyCode, EnforcementMode Mode,
    decimal Paid, decimal AdSpendCost, decimal NetPosition, decimal Exposure);

/// <summary>Company roll-up of net cash position for one currency — no FX, so currencies never mix.</summary>
public sealed record CurrencyRollup(
    string CurrencyCode, decimal NetPosition, decimal Paid, decimal AdSpendCost,
    decimal Exposure, int ClientCount, int NegativeClientCount);

/// <summary>Charges paid grouped by the client's package (Unassigned bucket for null), per currency.</summary>
public sealed record PackageRevenueSlice(string PackageName, string CurrencyCode, decimal Paid, int ClientCount);

/// <summary>Per-day counts for one proposed action, aligned to <see cref="EnforcementActivity.Days"/>.</summary>
public sealed record ActionDaySeries(ProposedActionType Action, IReadOnlyList<int> CountsPerDay);

/// <summary>Enforcement-verdict activity over a fixed day window (a time series for the chart).</summary>
public sealed record EnforcementActivity(IReadOnlyList<DateOnly> Days, IReadOnlyList<ActionDaySeries> Series)
{
    public bool HasAny => Series.Any(s => s.CountsPerDay.Any(c => c > 0));
    public int Total => Series.Sum(s => s.CountsPerDay.Sum());
}

/// <summary>Client-mix count for one AccountType × ContractType cell.</summary>
public sealed record ClientSegmentCount(AccountType AccountType, ContractType ContractType, int Count);

/// <summary>Master-client count on one enforcement-ladder rung.</summary>
public sealed record ModeCount(EnforcementMode Mode, int Count);

/// <summary>Everything the /analytics page renders, derived in one pass by <see cref="AnalyticsRules.Compute"/>.</summary>
public sealed record AnalyticsSnapshot
{
    public required DateTimeOffset ComputedAt { get; init; }
    /// <summary>Company net cash position per currency (USD row always present, first).</summary>
    public required IReadOnlyList<CurrencyRollup> NetPositionByCurrency { get; init; }
    /// <summary>Per-client net positions, most-negative first (the clients costing money lead).</summary>
    public required IReadOnlyList<ClientNetPosition> ClientNetPositions { get; init; }
    /// <summary>Company exposure per currency = Σ max(0, ad spend − paid). Mirrors the cockpit definition.</summary>
    public required IReadOnlyList<CurrencyExposure> ExposureByCurrency { get; init; }
    public required IReadOnlyList<PackageRevenueSlice> PackageRevenue { get; init; }
    public required EnforcementActivity EnforcementActivity { get; init; }
    public required IReadOnlyList<MappingGap> MappingGaps { get; init; }
    public required int MasterClientCount { get; init; }
    public required int VerifiedMasterClientCount { get; init; }
    public required IReadOnlyList<ClientSegmentCount> ClientSegments { get; init; }
    public required IReadOnlyList<ModeCount> ModeMix { get; init; }

    // ---- Convenience projections for the page ----
    public double VerifiedMappingPct =>
        MasterClientCount == 0 ? 0 : Math.Round(100.0 * VerifiedMasterClientCount / MasterClientCount, 1);
    public CurrencyRollup UsdRollup =>
        NetPositionByCurrency.First(r => r.CurrencyCode == "USD");
    public IEnumerable<CurrencyRollup> NonUsdRollups =>
        NetPositionByCurrency.Where(r => r.CurrencyCode != "USD");
    public decimal UsdExposure =>
        ExposureByCurrency.FirstOrDefault(e => e.CurrencyCode == "USD")?.Amount ?? 0m;
    public IEnumerable<PackageRevenueSlice> UsdPackageRevenue =>
        PackageRevenue.Where(p => p.CurrencyCode == "USD").OrderByDescending(p => p.Paid);
    /// <summary>True once any master client has a paid charge or ad spend — otherwise the page shows its empty state.</summary>
    public bool HasLedgerData => ClientNetPositions.Count > 0;
    public int TotalOpenGaps => MappingGaps.Sum(g => g.Count);
    public int TotalClients => ClientSegments.Sum(s => s.Count);
}
