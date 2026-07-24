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
    public AccountType AccountType { get; set; }
    public EnforcementMode EnforcementMode { get; set; } = EnforcementMode.Shadow;
    public Guid? PackageId { get; set; }
    public Package? Package { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];

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
    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
