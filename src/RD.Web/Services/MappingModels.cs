using RD.Domain;

namespace RD.Web.Services;

/// <summary>
/// One master-account client that still needs mapping work: either its four
/// required links aren't all present/active, or it has no current (non-invalidated)
/// MappingVerification. These are the rows in the wizard's left rail.
/// </summary>
public sealed record UnverifiedClientRow(
    Guid Id,
    string BusinessName,
    EnforcementMode Mode,
    bool HasStripeCustomer,
    bool HasStripeSubscription,
    bool HasMetaCampaign,
    bool HasGhlContact,
    bool HasCurrentVerification,
    int OpenPriorityInvestigations)
{
    /// <summary>All four enforcement-critical links present and active.</summary>
    public bool RequiredLinksComplete
        => HasStripeCustomer && HasStripeSubscription && HasMetaCampaign && HasGhlContact;

    /// <summary>Fully mapped AND verified — the finish line for a client.</summary>
    public bool IsVerified => RequiredLinksComplete && HasCurrentVerification;

    /// <summary>Human labels for the still-missing required links, for the gap chips.</summary>
    public IReadOnlyList<string> MissingLinks
    {
        get
        {
            var missing = new List<string>(4);
            if (!HasStripeCustomer) missing.Add("Stripe customer");
            if (!HasStripeSubscription) missing.Add("Stripe subscription");
            if (!HasMetaCampaign) missing.Add("Meta campaign");
            if (!HasGhlContact) missing.Add("GHL contact");
            return missing;
        }
    }
}

/// <summary>
/// One IdentityLink flattened for display, with a human-readable evidence line
/// resolved from the vendor projections (Stripe sub status/amount, campaign
/// name/status/spend, GHL last message, …).
/// </summary>
public sealed record LinkView(
    Guid Id,
    ExternalSystem System,
    LinkKind Kind,
    string ExternalId,
    int LinkVersion,
    bool Verified,
    bool Invalidated,
    DateTimeOffset? VerifiedAt,
    string Evidence,
    string? ExternalUrl = null);

/// <summary>One of the four required-link slots, with the active link filling it (or null when missing).</summary>
public sealed record RequiredSlot(
    ExternalSystem System,
    LinkKind Kind,
    string Label,
    string HelpText,
    LinkView? Active)
{
    public bool Present => Active is not null;
}

/// <summary>Resolved facts for one linked Meta campaign — the raw material of the blast-radius number.</summary>
public sealed record CampaignFact(
    string CampaignId,
    string? Name,
    string EffectiveStatus,
    decimal? DailyBudget,
    decimal RecentDailySpend,
    string Currency)
{
    public bool IsLive => string.Equals(EffectiveStatus, "ACTIVE", StringComparison.OrdinalIgnoreCase);
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? CampaignId : Name!;
}

/// <summary>The blast-radius summary — how much live ad spend this mapping would give the engine control over.</summary>
public sealed record BlastRadiusSummary(int LiveCampaignCount, decimal DailySpend, string Currency)
{
    /// <summary>The prominent one-liner for the wizard's blast-radius alert.</summary>
    public string Headline => BlastRadius.Describe(this);

    /// <summary>Live campaigns exist, so verifying arms a real pause lever — warn accordingly.</summary>
    public bool HasLiveSpend => LiveCampaignCount > 0;
}

/// <summary>Everything the wizard's detail panel renders for one client, loaded in one factory scope.</summary>
public sealed record ClientMappingDetail(
    Guid ClientId,
    string BusinessName,
    EnforcementMode Mode,
    AccountType AccountType,
    string CurrencyCode,
    IReadOnlyList<RequiredSlot> RequiredSlots,
    IReadOnlyList<LinkView> AllLinks,
    IReadOnlyList<CampaignFact> Campaigns,
    BlastRadiusSummary BlastRadius,
    bool HasCurrentVerification,
    DateTimeOffset? LastVerifiedAt,
    string? LastVerifiedBy)
{
    /// <summary>All four required slots are filled with an active link.</summary>
    public bool AllRequiredPresent => RequiredSlots.All(s => s.Present);

    public IEnumerable<CampaignFact> LiveCampaigns => Campaigns.Where(c => c.IsLive);
}

/// <summary>A candidate external id the wizard can suggest for a missing link, before name ranking.</summary>
public sealed record SuggestionCandidate(string ExternalId, string Label, DateTimeOffset SyncedAt, string? Detail = null);

/// <summary>One of a client's active Stripe customers, offered as the "keep this one" choice when resolving a duplicate-customer investigation.</summary>
public sealed record StripeCustomerCandidate(string ExternalId, string Detail);

/// <summary>A ranked link suggestion the operator can accept — best-effort, always labeled as a suggestion.</summary>
public sealed record LinkSuggestion(string ExternalId, string Label, string? Detail, double Score, string Rationale);

/// <summary>Result of any wizard write — a success/failure flag plus a human sentence for the snackbar.</summary>
public sealed record MappingWriteResult(bool Ok, string Message)
{
    public static MappingWriteResult Success(string message) => new(true, message);
    public static MappingWriteResult Fail(string message) => new(false, message);
}
