using RD.Domain;

namespace RD.Web.Services;

/// <summary>
/// The four cockpit interaction states (design doc, CEO review F6).
/// FirstRun = no sync has ever completed; Stale = Stripe or Meta data is past
/// the freshness bound; AllQuiet = fresh with nothing to do; Busy = fresh with
/// open proposed actions.
/// </summary>
public enum CockpitState { FirstRun, AllQuiet, Busy, Stale }

/// <summary>One ledger row for a master-account client, tagged with the client's billing currency.</summary>
public sealed record LedgerFact(Guid ClientId, string ClientCurrency, LedgerEntryType Type, decimal SignedAmount, DateTimeOffset OccurredAt);

/// <summary>Latest COMPLETED sync sweep per system (partial syncs never count — see SyncRun).</summary>
public sealed record CompletedSyncFact(ExternalSystem System, DateTimeOffset CompletedAt);

/// <summary>Open needs-investigation items grouped by kind.</summary>
public sealed record MappingGap(InvestigationKind Kind, int Count);

/// <summary>Unbilled exposure per currency — no FX conversion in v1, so currencies never mix.</summary>
public sealed record CurrencyExposure(string CurrencyCode, decimal Amount, int ClientCount);

/// <summary>Freshness of one external system's sync. IsStale is only meaningful for freshness-gated systems (Stripe, Meta).</summary>
public sealed record SystemFreshness(ExternalSystem System, DateTimeOffset? LastCompletedAt, bool IsStale);

/// <summary>Raw inputs for the pure cockpit derivation — loaded from the DB by CockpitStateService, constructed by hand in tests.</summary>
public sealed record CockpitData
{
    public int TotalClients { get; init; }
    public int MasterClientCount { get; init; }
    /// <summary>Clients with at least one ACTIVE Stripe Subscription identity link — the "linked" count on the first-run panel.</summary>
    public int ClientsWithActiveStripeSubscription { get; init; }
    /// <summary>Master-account clients with at least one ACTIVE Meta campaign (from the campaign projection).</summary>
    public int CampaignsLiveMasterClients { get; init; }
    /// <summary>OutboxActions in Pending or AwaitingApproval.</summary>
    public int OpenActionCount { get; init; }
    /// <summary>Clients whose LATEST decision proposes an enforcement action (Pause/Resume/DunningStep/Escalate) with no outbox row — in Shadow, the decisions ARE the queue.</summary>
    public int ShadowVerdictCount { get; init; }
    public IReadOnlyList<LedgerFact> MasterClientLedger { get; init; } = [];
    public IReadOnlyList<CompletedSyncFact> CompletedSyncRuns { get; init; } = [];
    public IReadOnlyList<MappingGap> OpenInvestigations { get; init; } = [];
}

/// <summary>Everything the cockpit page renders, derived in one pass by <see cref="CockpitRules.Compute"/>.</summary>
public sealed record CockpitSnapshot
{
    public required CockpitState State { get; init; }
    public required DateTimeOffset ComputedAt { get; init; }
    /// <summary>Per-currency unbilled exposure (USD row always present, first). Amount = Σ per client max(0, ad spend − charges paid).</summary>
    public required IReadOnlyList<CurrencyExposure> ExposureByCurrency { get; init; }
    /// <summary>Master clients whose most recent billing event (charge paid/failed) is a failure.</summary>
    public required int ClientsAtRisk { get; init; }
    public required int CampaignsLive { get; init; }
    public required int MasterClientCount { get; init; }
    public required int FailedPaymentsToday { get; init; }
    public required int OpenActionCount { get; init; }
    public int ShadowVerdictCount { get; init; }
    /// <summary>Everything the queue shows: staged outbox actions + shadow would-X verdicts.</summary>
    public int QueueCount => OpenActionCount + ShadowVerdictCount;
    public required IReadOnlyList<MappingGap> MappingGaps { get; init; }
    public required IReadOnlyList<SystemFreshness> SyncFreshness { get; init; }
    public required int ClientsLinked { get; init; }
    public required int TotalClients { get; init; }

    public decimal UsdExposure => ExposureByCurrency.FirstOrDefault(e => e.CurrencyCode == "USD")?.Amount ?? 0m;
    public IEnumerable<CurrencyExposure> NonUsdExposure => ExposureByCurrency.Where(e => e.CurrencyCode != "USD");
    public IEnumerable<SystemFreshness> StaleSystems => SyncFreshness.Where(f => f.IsStale);
    public int TotalOpenGaps => MappingGaps.Sum(g => g.Count);
}
