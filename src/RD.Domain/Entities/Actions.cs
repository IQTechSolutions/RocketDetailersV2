namespace RD.Domain.Entities;

/// <summary>
/// The single authoritative mutable action record and the only path for
/// external writes. Leased atomically (READPAST/UPDLOCK), executed with
/// per-vendor idempotency (Stripe keys, Meta/GHL read-back convergence),
/// revalidated against live policy immediately before the write.
/// </summary>
public class OutboxAction
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public Guid DecisionId { get; set; }
    public OutboxActionType ActionType { get; set; }
    public required string PayloadJson { get; set; }
    public required string IdempotencyKey { get; set; }
    public OutboxStatus Status { get; set; } = OutboxStatus.Pending;
    /// <summary>CAS token for approve/dismiss races across Cockpit/Slack — first writer wins.</summary>
    public int ActionVersion { get; set; } = 1;
    public string? ApprovedBy { get; set; }
    public ApprovalChannel? ApprovalChannel { get; set; }
    public string? DismissReason { get; set; }
    /// <summary>Ordered multi-step writes (GHL field-write before workflow-trigger) share a group; steps execute in SequenceNo order.</summary>
    public Guid? SequenceGroup { get; set; }
    public int SequenceNo { get; set; }
    /// <summary>Scheduling: delayed steps (delivery verification at +10 min) set this.</summary>
    public DateTimeOffset? NotBefore { get; set; }
    public string? LeaseOwner { get; set; }
    public long? FencingToken { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    /// <summary>Execution rejects the write if the live kill-switch epoch differs.</summary>
    public long ExpectedKillSwitchEpoch { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExecutedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>One open case per invoice (filtered unique). The window only advances on verified sends.</summary>
public class DunningCase
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public required string InvoiceExternalId { get; set; }
    public DateTimeOffset OpenedAt { get; set; }
    /// <summary>2 working days from first failed charge; final failure when this passes with no successful payment.</summary>
    public DateTimeOffset WindowExpiresAt { get; set; }
    public DunningCaseStatus Status { get; set; } = DunningCaseStatus.Open;
    public List<DunningAttempt> Attempts { get; set; } = [];
}

/// <summary>Append-only evidence: every dunning step with its verification trail.</summary>
public class DunningAttempt
{
    public Guid Id { get; set; }
    public Guid DunningCaseId { get; set; }
    public DunningCase? Case { get; set; }
    public int Step { get; set; }
    public DateTimeOffset DueAt { get; set; }
    public DateTimeOffset? TriggeredAt { get; set; }
    public string? VendorMessageId { get; set; }
    /// <summary>Set by the delayed GHL delivery-verification job. Null = unverified = window does not advance.</summary>
    public DateTimeOffset? VerifiedAt { get; set; }
    public string? FailureReason { get; set; }
    /// <summary>The working-hours calendar snapshot used to schedule this step (auditable cadence).</summary>
    public string? CadenceCalendarJson { get; set; }
}

/// <summary>
/// Pause lifecycle per entity (unique per action+entity). Restoration touches
/// only entities the app paused; anything externally modified is flagged.
/// </summary>
public class PauseOperation
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public Guid OutboxActionId { get; set; }
    public MetaEntityType EntityType { get; set; }
    public required string ExternalId { get; set; }
    public required string PriorStatus { get; set; }
    /// <summary>Read-back evidence captured at pause time.</summary>
    public string? RemoteVersion { get; set; }
    public PauseOperationState State { get; set; } = PauseOperationState.Paused;
    public DateTimeOffset PausedAt { get; set; }
    public DateTimeOffset? RestoredAt { get; set; }
}
