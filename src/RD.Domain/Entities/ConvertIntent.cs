namespace RD.Domain.Entities;

/// <summary>
/// The intent to convert a trial client into a paying subscriber. Created when a
/// closer clicks "Convert to subscriber" in the cockpit — the human intent signal
/// that gates every downstream billing mechanic. The app NEVER decides to bill on
/// its own; it only ever executes the mechanics after this record exists.
///
/// Mutable, RowVersion-guarded lifecycle record (NOT append-only): it transitions
/// through states as the billing + close mechanics run. Optimistic concurrency on
/// State keeps the Hangfire expiry sweep and the payment webhook from stomping each
/// other.
///
///   Drafted ──▶ AwaitingPayment ──▶ Paid ──▶ Closed
///      │              │                          │
///      │              ├──▶ Expired ──▶ Paid      └──▶ Reversed  (refund/chargeback)
///      │              │    (late payment recovers an expired intent)
///      │              └──▶ Failed   (payment failed)
///      └── never billed without a human clicking Convert
///
/// A0 (this shell) only ever creates the record in Drafted. The draft/branch logic
/// (A1), Stripe execution + `close` GHL tag write (B), and Auto promotion (C) come later.
/// </summary>
public class ConvertIntent
{
    public Guid Id { get; set; }

    public Guid ClientId { get; set; }
    public Client? Client { get; set; }

    /// <summary>
    /// Own vs Master, captured explicitly on the Convert dialog and written through to
    /// <see cref="Client.AccountType"/> — never left to the enum default (Master), which
    /// would wrongly subject an own-account client to ad-spend enforcement.
    /// </summary>
    public AccountType AccountType { get; set; }

    /// <summary>
    /// The package whose effective <c>PackageVersion.StripePriceId</c> prices this
    /// conversion. Defaults from <see cref="Client.PackageId"/> at Convert time; null
    /// until a package is chosen.
    /// </summary>
    public Guid? PackageId { get; set; }
    public Package? Package { get; set; }

    /// <summary>
    /// The Stripe customer this subscription bills — first in the client's cluster, else
    /// app-created. Null in the A0 shell; resolved by A1's draft logic.
    /// </summary>
    public string? StripeCustomerId { get; set; }

    /// <summary>
    /// The Stripe subscription created at billing-execute (rung B). Null until executed; it's how the
    /// first-payment webhook correlates a paid invoice back to THIS conversion.
    /// </summary>
    public string? StripeSubscriptionId { get; set; }

    public ConvertIntentState State { get; set; } = ConvertIntentState.Drafted;

    /// <summary>The staged Stripe action (A1 draft), serialized. Null in the A0 shell.</summary>
    public string? DraftedActionJson { get; set; }

    /// <summary>
    /// Shadow record for the (spike-gated) `close` GHL tag write: the client's GHL contact resolved at
    /// first-payment promotion. Captures the write target so the live write knows where to write and
    /// resolution can be validated against real conversions before anything fires. Null if the client
    /// has no linked GHL contact.
    /// </summary>
    public string? CloseTagContactId { get; set; }

    /// <summary>ASP.NET Identity user id of the closer who clicked Convert.</summary>
    public string? CreatedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>When an AwaitingPayment intent goes stale and the sweep expires it. Null while Drafted.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Optimistic concurrency token — the expiry sweep and the payment webhook must not stomp each other on State.</summary>
    public byte[] RowVersion { get; set; } = [];
}
