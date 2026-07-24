namespace RD.Domain.Entities;

// Vendor projections: the operational read model that feeds ClientState.
// These are CACHED REPLICAS of vendor facts with staleness metadata — SQL is
// authoritative for mappings/policy/approvals/audit/ledger, never for these.
// Every row carries SourceSyncedAt; the freshness gate reads it per client.

public class StripeSubscriptionProj
{
    public required string SubscriptionId { get; set; }
    public Guid? ClientId { get; set; }
    public required string CustomerId { get; set; }
    public required string Status { get; set; }
    public string? PriceInterval { get; set; }
    public decimal? Amount { get; set; }
    public string CurrencyCode { get; set; } = "USD";
    public DateTimeOffset? CanceledAt { get; set; }
    public DateTimeOffset SourceSyncedAt { get; set; }
}

public class StripeInvoiceProj
{
    public required string InvoiceId { get; set; }
    public Guid? ClientId { get; set; }
    public required string CustomerId { get; set; }
    public string? SubscriptionId { get; set; }
    public required string Status { get; set; }
    public decimal AmountDue { get; set; }
    public decimal AmountPaid { get; set; }
    public string CurrencyCode { get; set; } = "USD";
    /// <summary>The URL dunning messages carry — pays THIS invoice (never a Payment Link).</summary>
    public string? HostedInvoiceUrl { get; set; }
    public DateTimeOffset CreatedAtSource { get; set; }
    public DateTimeOffset? DueDate { get; set; }
    public DateTimeOffset SourceSyncedAt { get; set; }
}

public class MetaCampaignProj
{
    public required string CampaignId { get; set; }
    public Guid? ClientId { get; set; }
    public required string AdAccountId { get; set; }
    public string? Name { get; set; }
    public required string Status { get; set; }
    public required string EffectiveStatus { get; set; }
    public decimal? DailyBudget { get; set; }
    /// <summary>updated_time from Meta — read-back convergence evidence.</summary>
    public string? RemoteVersion { get; set; }
    public DateTimeOffset SourceSyncedAt { get; set; }
}

public class MetaInsightDailyProj
{
    public required string CampaignId { get; set; }
    public DateOnly Date { get; set; }
    public Guid? ClientId { get; set; }
    public decimal Spend { get; set; }
    public string CurrencyCode { get; set; } = "USD";
    public int Clicks { get; set; }
    public int Leads { get; set; }
    public DateTimeOffset SourceSyncedAt { get; set; }
}

/// <summary>Outbound GHL messages observed via the conversations API — the F2 delivery-verification evidence.</summary>
public class GhlMessageProj
{
    public required string MessageId { get; set; }
    public Guid? ClientId { get; set; }
    public required string LocationId { get; set; }
    public required string ContactId { get; set; }
    public required string MessageType { get; set; }
    public string? BodyPreview { get; set; }
    public DateTimeOffset SentAt { get; set; }
    public DateTimeOffset SourceSyncedAt { get; set; }
}
