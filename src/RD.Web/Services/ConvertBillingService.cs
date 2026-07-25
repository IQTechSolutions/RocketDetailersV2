using Microsoft.EntityFrameworkCore;
using RD.Domain;
using RD.Domain.Entities;
using RD.Infrastructure;
using RD.Infrastructure.Enforcement;
using RD.Infrastructure.Gateways;

namespace RD.Web.Services;

/// <summary>
/// Rung B (Assist) of Convert→Bill→Close: a human one-click executes the drafted subscription. This
/// is the money-write, so it is ONLY ever invoked by an explicit operator action — never automatically.
///
/// Guards, in order: the global kill switch must be off; the intent must still be Drafted (so a
/// double-click / retry can't bill twice); and the draft must be auto-draftable (own-account, USD, a
/// package with a Stripe price). It creates the Stripe customer if the client has none — writing the
/// IdentityLink BEFORE the first-payment webhook can arrive so the webhook resolves — then creates the
/// subscription with a per-intent Idempotency-Key and the convert_intent_id metadata the webhook
/// correlates on, and moves the intent to AwaitingPayment.
///
/// Retry-safe: both the customer and subscription creates use stable per-intent idempotency keys, so a
/// re-run (after a lost ack) resolves to the same Stripe objects — never a double charge. RowVersion on
/// ConvertIntent turns a concurrent execute into a caught conflict, not a second subscription.
/// </summary>
public class ConvertBillingService(
    IDbContextFactory<RdDbContext> factory, IStripeGateway stripe, KillSwitchService killSwitch, IClock clock)
{
    /// <summary>How long an unpaid conversion waits before the expiry sweep reaps it.</summary>
    private static readonly TimeSpan AwaitingPaymentWindow = TimeSpan.FromDays(7);

    public async Task<ConvertResult> ExecuteAsync(Guid intentId, string? actor = null, CancellationToken ct = default)
    {
        if (await killSwitch.IsEngagedAsync(ct))
            return new ConvertResult(false, "The global kill switch is engaged — no billing writes until it's released.");

        await using var db = await factory.CreateDbContextAsync(ct);

        var intent = await db.ConvertIntents.FirstOrDefaultAsync(i => i.Id == intentId, ct);
        if (intent is null) return new ConvertResult(false, "Conversion not found (maybe it changed in another tab).");
        if (intent.State != ConvertIntentState.Drafted)
            return new ConvertResult(false, $"Conversion is already {intent.State} — nothing to bill.");

        var client = await db.Clients.FirstOrDefaultAsync(c => c.Id == intent.ClientId, ct);
        if (client is null) return new ConvertResult(false, "Client not found.");

        // Recompute the draft against current data (a price set after Convert is now reflected) — refuse
        // if it isn't auto-draftable rather than billing on a stale or incomplete draft.
        var input = await ConvertService.LoadDraftInputAsync(db, client, intent.AccountType, intent.PackageId, ct);
        var draft = ConvertDrafter.Draft(input);
        if (!draft.Ready)
            return new ConvertResult(false, $"Can't bill yet: {string.Join(" ", draft.Blockers)}");

        // 1. Resolve or create the Stripe customer. Persist the IdentityLink immediately so a webhook
        //    that lands before step 3 can still resolve to this client.
        var customerId = draft.StripeCustomerId;
        if (string.IsNullOrEmpty(customerId))
        {
            customerId = await stripe.CreateCustomerAsync(
                client.ContactName ?? client.BusinessName, client.Email, $"convert-{intent.Id}-cust", ct);
            db.IdentityLinks.Add(NewLink(client.Id, LinkKind.Customer, customerId));
            await db.SaveChangesAsync(ct);
        }

        // 2. Create the subscription — idempotency key + intent metadata make a retry/double-click resolve
        //    to a single subscription and let the first-payment webhook correlate back to this intent.
        var sub = await stripe.CreateSubscriptionAsync(
            customerId!, draft.StripePriceId!,
            new Dictionary<string, string> { ["convert_intent_id"] = intent.Id.ToString() },
            $"convert-{intent.Id}", ct);

        var now = clock.UtcNow;
        db.IdentityLinks.Add(NewLink(client.Id, LinkKind.Subscription, sub.Id));

        // 3. Move the intent to AwaitingPayment. RowVersion guards a concurrent execute.
        intent.State = ConvertIntentState.AwaitingPayment;
        intent.StripeCustomerId = customerId;
        intent.StripeSubscriptionId = sub.Id; // the first-payment webhook correlates on this
        intent.ExpiresAt = now + AwaitingPaymentWindow;
        intent.UpdatedAt = now;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another execute won the race. The shared idempotency key means Stripe created ONE
            // subscription, so there is no double charge — just report the conflict.
            return new ConvertResult(false, "This conversion was just billed by another action — no double charge (idempotent).");
        }

        return new ConvertResult(true, $"Subscription {sub.Id} created — awaiting first payment.", intent.Id);
    }

    /// <summary>
    /// The off-switch: cancel the conversion's Stripe subscription and mark it Reversed. Human-approved
    /// (an operator action), so it is NOT kill-switch-gated — stopping a charge only ever reduces exposure.
    /// Idempotent at Stripe (a missing/already-canceled subscription is treated as done).
    /// </summary>
    public async Task<ConvertResult> CancelAsync(Guid intentId, string? actor = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var intent = await db.ConvertIntents.FirstOrDefaultAsync(i => i.Id == intentId, ct);
        if (intent is null) return new ConvertResult(false, "Conversion not found (maybe it changed in another tab).");
        if (string.IsNullOrEmpty(intent.StripeSubscriptionId))
            return new ConvertResult(false, "No subscription to cancel — this conversion hasn't been billed yet.");
        if (intent.State is ConvertIntentState.Reversed or ConvertIntentState.Expired or ConvertIntentState.Failed)
            return new ConvertResult(false, $"Conversion is already {intent.State} — nothing to cancel.");

        await stripe.CancelSubscriptionAsync(intent.StripeSubscriptionId, ct);

        intent.State = ConvertIntentState.Reversed;
        intent.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);

        return new ConvertResult(true, $"Subscription {intent.StripeSubscriptionId} canceled — conversion reversed.", intent.Id);
    }

    private IdentityLink NewLink(Guid clientId, LinkKind kind, string externalId) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        System = ExternalSystem.Stripe,
        Kind = kind,
        ExternalId = externalId,
        VerifiedAt = clock.UtcNow, // app-created + written here → trusted
        CreatedAt = clock.UtcNow,
    };
}
