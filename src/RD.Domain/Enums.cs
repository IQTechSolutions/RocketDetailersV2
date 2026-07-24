namespace RD.Domain;

public enum ContractType { Trial = 0, Paid = 1 }

public enum AccountType { Master = 0, Own = 1 }

/// <summary>
/// Per-client enforcement ladder. Promotion: Shadow → Assist (mapping verified)
/// → Auto (14 clean days + one exercised failure path). Demotion to Shadow on
/// mapping drift is automatic; the global kill switch reverts everyone.
/// </summary>
public enum EnforcementMode { Shadow = 0, Assist = 1, Auto = 2 }

public enum ExternalSystem { Stripe = 0, Meta = 1, Ghl = 2, ClickUp = 3 }

public enum LinkKind { Customer = 0, Subscription = 1, Campaign = 2, Contact = 3, Task = 4, AdAccount = 5 }

public enum LedgerEntryType { ChargePaid = 0, ChargeFailed = 1, Refund = 2, Dispute = 3, AdSpend = 4, Adjustment = 5 }

public enum ProposedActionType { None = 0, Pause = 1, Resume = 2, DunningStep = 3, Escalate = 4, Investigate = 5 }

/// <summary>
/// OutboxAction is the single authoritative *mutable* action record
/// (Decision stays immutable). Status flow:
///
///   Pending ──▶ AwaitingApproval ──▶ Approved ──▶ Leased ──▶ Executed
///      │              │                                │
///      │              └──▶ Dismissed                   ├──▶ Superseded (revalidation failed)
///      │                                               ├──▶ Failed ──▶ (retry) ──▶ DeadLettered
///      └── (Auto mode skips approval) ─────────────────┘
/// </summary>
public enum OutboxStatus
{
    Pending = 0, AwaitingApproval = 1, Approved = 2, Dismissed = 3,
    Leased = 4, Executed = 5, Superseded = 6, Failed = 7, DeadLettered = 8
}

public enum ApprovalChannel { Cockpit = 0, Slack = 1 }

public enum OutboxActionType
{
    PauseCampaign = 0, ResumeCampaign = 1,
    WriteGhlField = 2, TriggerGhlWorkflow = 3,
    VerifyGhlDelivery = 4, SendSlackAlert = 5, SendFallbackEmail = 6
}

public enum DunningCaseStatus { Open = 0, Satisfied = 1, FinalFailed = 2, Escalated = 3, Closed = 4 }

public enum PauseOperationState { Paused = 0, ExternallyModified = 1, Restored = 2, RestoreFailed = 3 }

public enum WebhookStatus { Received = 0, Processed = 1, Poisoned = 2 }

public enum SyncRunStatus { Running = 0, Completed = 1, Failed = 2 }

public enum MetaEntityType { Campaign = 0, AdSet = 1, Ad = 2 }

public enum InvestigationStatus { Open = 0, Resolved = 1, Dismissed = 2 }

public enum InvestigationKind
{
    UnmappedIdentity = 0, DuplicateStripeCustomer = 1, ExternallyPausedPayment = 2,
    CanceledSubPayment = 3, NonUsdCurrency = 4, MissingTrialExpiry = 5,
    StaleSync = 6, DeliveryUnverified = 7, ExposureCapExceeded = 8,
    SpendAnomaly = 9, ImportConflict = 10, PaymentArrangementUnclear = 11, Other = 99
}

/// <summary>
/// State of a client's payment arrangement (expected amount + cadence).
/// Inferred where the payment history is regular enough; otherwise a human confirms.
/// </summary>
public enum ArrangementStatus { Unknown = 0, Inferred = 1, NeedsReview = 2, Confirmed = 3 }

public enum TrialOutcome { Active = 0, Promoted = 1, Expired = 2, Extended = 3 }
