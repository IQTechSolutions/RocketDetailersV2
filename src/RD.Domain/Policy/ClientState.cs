namespace RD.Domain.Policy;

/// <summary>One campaign's live status, with pause provenance.</summary>
public sealed record CampaignState(string CampaignId, string EffectiveStatus, bool PausedByApp)
{
    public bool IsActive => EffectiveStatus is "ACTIVE";
    public bool IsPaused => EffectiveStatus is "PAUSED" or "CAMPAIGN_PAUSED";
}

/// <summary>Dunning-case snapshot. The window only ever advanced on verified sends — the builder guarantees that invariant.</summary>
public sealed record DunningState(
    int LastStep,
    DateTimeOffset WindowExpiresAt,
    DateTimeOffset? NextStepDueAt,
    bool AllSendsVerified,
    DateTimeOffset? OldestUnverifiedSince);

/// <summary>
/// Complete input to one policy evaluation — everything the engine may
/// consider, snapshotted. Persisted as Decision.StateSnapshotJson so every
/// decision is replayable as a golden test.
/// </summary>
public sealed record ClientState
{
    public required Guid ClientId { get; init; }
    public required EnforcementMode Mode { get; init; }
    public required ContractType Contract { get; init; }
    public required AccountType Account { get; init; }
    public string CurrencyCode { get; init; } = "USD";
    public bool MappingVerified { get; init; }

    // Trial (suppresses payment enforcement while genuinely active)
    public bool HasActiveTrial { get; init; }
    public DateTimeOffset? TrialExpiresAt { get; init; }
    public decimal TrialSpend { get; init; }
    public decimal? TrialSpendCap { get; init; }

    // Billing (from Stripe projections)
    /// <summary>"active", "past_due", "canceled", "unpaid" — null when no subscription linked.</summary>
    public string? SubscriptionStatus { get; init; }
    public int OpenUnpaidInvoices { get; init; }
    /// <summary>A payment arrived for a canceled subscription — a link payment cannot reactivate it.</summary>
    public bool PaymentReceivedForCanceledSub { get; init; }

    // Dunning
    public DunningState? Dunning { get; init; }
    /// <summary>A failed charge exists with no open dunning case yet.</summary>
    public bool HasNewFailedCharge { get; init; }

    // Campaigns (from Meta projections + PauseOperations)
    public IReadOnlyList<CampaignState> Campaigns { get; init; } = [];

    // Money & freshness
    public decimal Exposure { get; init; }
    public decimal MaxLossCap { get; init; } = 200m;

    // Arrangement balance (Step 3): cumulative money in vs. master-account ad spend,
    // measured against the client's payment arrangement.
    public decimal TotalPaid { get; init; }
    public decimal TotalAdSpend { get; init; }
    /// <summary>Expected payment per cadence (inferred or confirmed); null when there is no arrangement.</summary>
    public decimal? ArrangementAmount { get; init; }
    public ArrangementStatus ArrangementStatus { get; init; } = ArrangementStatus.Unknown;

    public DateTimeOffset? StripeSyncedAt { get; init; }
    public DateTimeOffset? MetaSyncedAt { get; init; }
    public required DateTimeOffset EvaluatedAt { get; init; }
}

/// <summary>
/// The policy's verdict. Mode decides what the engine DOES with it
/// (Shadow logs, Assist queues for approval, Auto queues for execution) —
/// that dispatch lives outside the pure function.
/// </summary>
public sealed record PolicyDecision(
    ProposedActionType Action,
    string Reason,
    InvestigationKind? Investigation = null,
    bool DemoteToShadow = false,
    int? DunningStep = null,
    IReadOnlyList<string>? TargetCampaignIds = null);
