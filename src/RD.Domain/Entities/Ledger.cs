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
    /// <summary>Human-readable evidence line for the cockpit "Why?" button.</summary>
    public string? Reason { get; set; }
}

/// <summary>Needs-investigation work queue — drift becomes human work, not silence.</summary>
public class InvestigationItem
{
    public Guid Id { get; set; }
    public Guid? ClientId { get; set; }
    public InvestigationKind Kind { get; set; }
    public required string Detail { get; set; }
    public InvestigationStatus Status { get; set; } = InvestigationStatus.Open;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public string? ResolvedBy { get; set; }
    public string? ResolutionNote { get; set; }
}
