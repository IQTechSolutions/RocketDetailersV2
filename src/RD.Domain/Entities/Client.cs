namespace RD.Domain.Entities;

public class Client
{
    public Guid Id { get; set; }
    public required string BusinessName { get; set; }
    public string? ContactName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Country { get; set; }
    /// <summary>ISO 4217. Policy enforces USD-only in v1; non-USD rows route to needs-investigation.</summary>
    public string CurrencyCode { get; set; } = "USD";
    public ContractType ContractType { get; set; }
    // Safe default: Own = the enforcement-INERT type. The AccountType enum's zero
    // value is Master (the ad-spend-enforcement-ACTIVE type), so a Client created
    // without setting this would otherwise persist as Master and be swept into
    // arrangement-balance pauses (EligibilityPolicy rule 6). Defaulting to Own
    // means a client is never *silently* enforcement-active — Master is only ever
    // reached by an explicit, human-driven decision (import "Master Ad Account?",
    // mark-master CLI, or the cockpit Convert dialog). Never rely on this default
    // to mean "Master."
    public AccountType AccountType { get; set; } = AccountType.Own;
    public EnforcementMode EnforcementMode { get; set; } = EnforcementMode.Shadow;
    public Guid? PackageId { get; set; }
    public Package? Package { get; set; }

    // Payment arrangement — the yardstick the balance policy measures against.
    // Inferred from the client's payment cadence; low-confidence inferences are
    // flagged for a human (ArrangementStatus.NeedsReview) rather than trusted.
    public decimal? ExpectedAmount { get; set; }
    public int? CadenceDays { get; set; }
    public ArrangementStatus ArrangementStatus { get; set; } = ArrangementStatus.Unknown;

    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];

    // Merge: when two client records are really one business (a duplicate created
    // by accident), the duplicate is retired into the survivor rather than deleted
    // — its third-party records are live and must keep being monitored. Its links
    // are re-parented onto the survivor (so future movement auto-attributes there);
    // this pointer marks the duplicate as retired so it drops out of every active
    // surface (policy evaluation, directory, cockpit, analytics) while its
    // append-only ledger history rolls up into the survivor. Reversible: clear the
    // pointer and move the links back.
    public Guid? MergedIntoClientId { get; set; }
    public DateTimeOffset? MergedAt { get; set; }

    public List<IdentityLink> IdentityLinks { get; set; } = [];
    public List<TrialPeriod> TrialPeriods { get; set; } = [];
}

/// <summary>Trial state lives here, never on Client — multiple trials, extensions, and promotions must survive history.</summary>
public class TrialPeriod
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public Client? Client { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    /// <summary>A trial with a null expiry is a needs-investigation item, not enforcement-exempt.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
    public decimal? SpendCapSnapshot { get; set; }
    public TrialOutcome Outcome { get; set; } = TrialOutcome.Active;
}

public class IdentityLink
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public Client? Client { get; set; }
    public ExternalSystem System { get; set; }
    public LinkKind Kind { get; set; }
    public required string ExternalId { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public DateTimeOffset? InvalidatedAt { get; set; }
    /// <summary>Bumped on every change; MappingVerification pins the versions it verified.</summary>
    public int LinkVersion { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A verification batch: who verified which links (at which versions), with what evidence.</summary>
public class MappingVerification
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public Client? Client { get; set; }
    /// <summary>JSON: [{ linkId, linkVersion }]. Any required-link change invalidates this batch and demotes the client.</summary>
    public required string VerifiedLinksJson { get; set; }
    public string? EvidenceNote { get; set; }
    public required string VerifiedBy { get; set; }
    public bool BlastRadiusAcknowledged { get; set; }
    public DateTimeOffset VerifiedAt { get; set; }
    public DateTimeOffset? InvalidatedAt { get; set; }
}

public class Package
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public bool IsActive { get; set; } = true;
    public List<PackageVersion> Versions { get; set; } = [];
}

/// <summary>Effective-dated terms. Decisions and ledger periods snapshot the version they used — edits never rewrite history.</summary>
public class PackageVersion
{
    public Guid Id { get; set; }
    public Guid PackageId { get; set; }
    public Package? Package { get; set; }
    public DateTimeOffset EffectiveFrom { get; set; }
    public decimal DailyRate { get; set; }
    public decimal DailyBudget { get; set; }
    public decimal? TrialSpendCap { get; set; }
    public string CurrencyCode { get; set; } = "USD";
    public string? OfferName { get; set; }

    /// <summary>
    /// The Stripe Price this version bills at conversion — the own-account flat service fee.
    /// Feeds the Convert→Bill→Close draft (A1) and the billing amount-cap band. Null until set
    /// via the price-book admin. Master-account variable (ad-spend-covering) billing does NOT
    /// use a fixed price — that is a separate follow-on wedge.
    /// </summary>
    public string? StripePriceId { get; set; }

    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
