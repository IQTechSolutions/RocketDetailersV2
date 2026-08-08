namespace RD.Domain.Entities;

/// <summary>
/// Append-only audit history for the Stripe customer preferred for future
/// billing. Changing this preference never invalidates an identity link and
/// therefore never narrows sync, ledger, or enforcement coverage.
/// </summary>
public class StripeCustomerPreferenceChange
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public Client? Client { get; set; }

    public string? PreviousStripeCustomerId { get; set; }
    public required string PreferredStripeCustomerId { get; set; }

    public required string ChangedBy { get; set; }
    public DateTimeOffset ChangedAt { get; set; }
    public required string Reason { get; set; }

    /// <summary>The queue item that prompted the choice, when applicable.</summary>
    public Guid? InvestigationItemId { get; set; }
}
