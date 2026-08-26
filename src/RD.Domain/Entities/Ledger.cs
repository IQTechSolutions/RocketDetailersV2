namespace RD.Domain.Entities;

/// <summary>
/// Append-only receivables-vs-spend ledger. Corrections are compensating
/// entries, never updates. Roll-ups are per currency — no FX conversion.
/// Ingestion is idempotent via unique (SourceSystem, SourceObjectId, Type).
/// </summary>
public class LedgerEntry
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    /// <summary>When the economic event happened at the vendor.</summary>
    public DateTimeOffset OccurredAt { get; set; }
    /// <summary>When we ingested it.</summary>
    public DateTimeOffset RecordedAt { get; set; }
    public LedgerEntryType Type { get; set; }
    /// <summary>Signed convention: charges paid are positive (money in), ad spend/refunds/disputes negative (money out/reversed).</summary>
    public decimal SignedAmount { get; set; }
    public string CurrencyCode { get; set; } = "USD";
    public ExternalSystem SourceSystem { get; set; }
    public required string SourceObjectId { get; set; }
}

/// <summary>
/// Immutable record of one policy evaluation. All mutable action state lives
/// on OutboxAction — this row never changes after insert.
/// </summary>
public class Decision
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public DateTimeOffset EvaluatedAt { get; set; }
    public required string PolicyVersion { get; set; }
    /// <summary>Full ClientState snapshot (JSON) — decisions are replayable as golden tests.</summary>
    public required string StateSnapshotJson { get; set; }
    public ProposedActionType ProposedAction { get; set; }
    public EnforcementMode Mode { get; set; }
    /// <summary>Canonical JSON array of exact campaign ids targeted by this verdict. Null only on pre-migration history.</summary>
    public string? TargetCampaignIdsJson { get; set; }
    /// <summary>Human-readable evidence line for the cockpit "Why?" button.</summary>
    public string? Reason { get; set; }
}

/// <summary>
/// One lifecycle incident for an exact campaign/action recommendation while a
/// client is in Shadow. EndedAt closes the incident when the recommendation or
/// target state changes; repeated policy heartbeats do not create new rows.
/// </summary>
public class MetaShadowPrediction
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public Guid DecisionId { get; set; }
    public string? CampaignId { get; set; }
    public ProposedActionType ProposedAction { get; set; }
    public required string DesiredStatus { get; set; }
    public MetaShadowTargetState TargetState { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
}

/// <summary>
/// Immutable, idempotently-ingested fact from Meta's GET-only ad-account
/// activity edge. SourceFingerprint replaces the missing stable activity id.
/// </summary>
public class MetaActivityFact
{
    public Guid Id { get; set; }
    public required string SourceFingerprint { get; set; }
    public required string AdAccountId { get; set; }
    public DateTimeOffset EventTime { get; set; }
    public required string EventType { get; set; }
    public required string ObjectId { get; set; }
    public string? ObjectName { get; set; }
    public string? ObjectType { get; set; }
    public string? ActorId { get; set; }
    public string? ActorName { get; set; }
    public string? ApplicationId { get; set; }
    public string? ApplicationName { get; set; }
    public string? Tool { get; set; }
    public string? TranslatedEventType { get; set; }
    public string? OldStatus { get; set; }
    public string? NewStatus { get; set; }
    public string? ExtraDataJson { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
}

/// <summary>Needs-investigation work queue — drift becomes human work, not silence.</summary>
public class InvestigationItem
{
    public Guid Id { get; set; }
    public Guid? ClientId { get; set; }
    public InvestigationKind Kind { get; set; }
    public required string Detail { get; set; }
    /// <summary>The external system this item concerns (Stripe/Meta/GHL/ClickUp), when it points at a specific vendor identity. Null for engine-internal items.</summary>
    public ExternalSystem? System { get; set; }
    /// <summary>The external id the operator needs to double-check (cus_/sub_/campaign/contact/task), when known. Enables a direct vendor deep-link instead of parsing <see cref="Detail"/>.</summary>
    public string? ExternalId { get; set; }
    public InvestigationStatus Status { get; set; } = InvestigationStatus.Open;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public string? ResolvedBy { get; set; }
    public string? ResolutionNote { get; set; }
}
