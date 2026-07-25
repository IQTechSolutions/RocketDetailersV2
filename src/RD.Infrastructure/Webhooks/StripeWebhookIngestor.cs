using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RD.Domain;
using RD.Domain.Entities;
using RD.Infrastructure.Sync;

namespace RD.Infrastructure.Webhooks;

public enum WebhookIngestResult
{
    /// <summary>Recorded and side effects applied (or a harmless no-op for unknown types).</summary>
    Processed,

    /// <summary>The event id was already in the inbox — deduped, never reprocessed.</summary>
    AlreadyProcessed,

    /// <summary>Processing threw; the event is recorded as Poisoned for deliberate replay, never lost.</summary>
    Poisoned,
}

/// <summary>
/// The recoverable webhook inbox for Stripe. A verified event is deduped and
/// applied inside ONE transaction: the inbox row (Received), the projection/ledger
/// mutations, and the terminal Processed status commit together — so a redelivery
/// can never double-apply money (it dedups on the committed row) and the inbox can
/// never show Processed without its side effects, nor side effects without a marker.
///
/// If processing throws, the transaction is rolled back (no half-applied side
/// effects) and the event is re-recorded as Poisoned in its own transaction, with
/// the error and an attempt count — visible in the work queue, replayable, never
/// silently dropped. Every outcome is 200-worthy to Stripe: a poisoned event is
/// recorded, not an HTTP error that invites an infinite redelivery storm.
/// </summary>
public sealed class StripeWebhookIngestor(
    IDbContextFactory<RdDbContext> dbFactory,
    IClock clock,
    ILogger<StripeWebhookIngestor> logger)
{
    private const int ErrorMaxLength = 2000;

    /// <param name="eventId">Stripe event.id — the dedup key.</param>
    /// <param name="eventType">Stripe event.type — routes the side effect.</param>
    /// <param name="payloadJson">The verified raw event body (signature was over these bytes).</param>
    public async Task<WebhookIngestResult> IngestAsync(
        string eventId, string eventType, string payloadJson, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = clock.UtcNow;

        var inbox = new WebhookInboxItem
        {
            Id = Guid.NewGuid(),
            System = ExternalSystem.Stripe,
            ExternalEventId = eventId,
            EventType = eventType,
            PayloadJson = payloadJson,
            Status = WebhookStatus.Received,
            ReceivedAt = now,
        };

        var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            db.WebhookInbox.Add(inbox);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (IsDuplicateKey(ex))
            {
                // The unique (System, ExternalEventId) index rejected the insert:
                // a prior or concurrent delivery already owns this event. Never reprocess.
                await tx.RollbackAsync(ct);
                return WebhookIngestResult.AlreadyProcessed;
            }

            try
            {
                inbox.EntityRef = await ApplySideEffectsAsync(db, eventType, payloadJson, now, ct);
                inbox.Status = WebhookStatus.Processed;
                inbox.ProcessedAt = now;
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return WebhookIngestResult.Processed;
            }
            catch (Exception processingError)
            {
                // Side effects must not half-commit: discard them AND the Received row.
                await tx.RollbackAsync(ct);
                logger.LogError(processingError,
                    "Stripe webhook {EventId} ({EventType}) poisoned during processing", eventId, eventType);
                await RecordPoisonAsync(eventId, eventType, payloadJson, processingError, ct);
                return WebhookIngestResult.Poisoned;
            }
        }
        finally
        {
            await tx.DisposeAsync();
        }
    }

    /// <summary>
    /// Records the failed event as Poisoned in a fresh context/transaction so it
    /// survives the rollback above and stays visible for replay. Best-effort save
    /// (CancellationToken.None): even a cancelled request records its poison.
    /// </summary>
    private async Task RecordPoisonAsync(
        string eventId, string eventType, string payloadJson, Exception error, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.WebhookInbox.Add(new WebhookInboxItem
        {
            Id = Guid.NewGuid(),
            System = ExternalSystem.Stripe,
            ExternalEventId = eventId,
            EventType = eventType,
            PayloadJson = payloadJson,
            Status = WebhookStatus.Poisoned,
            Attempts = 1,
            LastError = Truncate(error.ToString(), ErrorMaxLength),
            ReceivedAt = clock.UtcNow,
        });
        try
        {
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            // A concurrent delivery already recorded this event id — nothing to add.
        }
    }

    /// <summary>
    /// Operator replay of a poisoned event: re-run the side effects for a stored
    /// inbox row and, on success, flip it to Processed. Side effects are idempotent
    /// (ledger inserts dedup on source id; projections upsert), so a replay can
    /// never double-apply money. On failure the row stays Poisoned with a bumped
    /// attempt count and the fresh error — same guarantees as the receive path.
    /// </summary>
    public async Task<WebhookIngestResult> ReplayAsync(Guid inboxItemId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = clock.UtcNow;

        var item = await db.WebhookInbox.FirstOrDefaultAsync(w => w.Id == inboxItemId, ct);
        if (item is null || item.Status == WebhookStatus.Processed)
            return WebhookIngestResult.AlreadyProcessed;

        var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            try
            {
                item.EntityRef = await ApplySideEffectsAsync(db, item.EventType, item.PayloadJson, now, ct);
                item.Status = WebhookStatus.Processed;
                item.ProcessedAt = now;
                item.Attempts += 1;
                item.LastError = null;
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return WebhookIngestResult.Processed;
            }
            catch (Exception processingError)
            {
                await tx.RollbackAsync(ct);
                logger.LogError(processingError,
                    "Replay of webhook {Id} ({EventType}) failed again", item.Id, item.EventType);
                await BumpPoisonAttemptAsync(inboxItemId, processingError, ct);
                return WebhookIngestResult.Poisoned;
            }
        }
        finally
        {
            await tx.DisposeAsync();
        }
    }

    /// <summary>Records a failed replay on the existing poison row (attempts + error) in a fresh context, surviving the rollback above.</summary>
    private async Task BumpPoisonAttemptAsync(Guid inboxItemId, Exception error, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await db.WebhookInbox.FirstOrDefaultAsync(w => w.Id == inboxItemId, CancellationToken.None);
        if (item is null) return;
        item.Attempts += 1;
        item.LastError = Truncate(error.ToString(), ErrorMaxLength);
        item.Status = WebhookStatus.Poisoned;
        try { await db.SaveChangesAsync(CancellationToken.None); }
        catch (DbUpdateException) { /* best-effort */ }
    }

    private async Task<string?> ApplySideEffectsAsync(
        RdDbContext db, string eventType, string payloadJson, DateTimeOffset now, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(payloadJson);
        switch (eventType)
        {
            case "invoice.paid":
            case "invoice.payment_failed":
            case "invoice.finalized":
            case "invoice.updated":
                return await ProcessInvoiceAsync(db, GetDataObject(doc), eventType, now, ct);

            case "customer.subscription.updated":
            case "customer.subscription.deleted":
                return await ProcessSubscriptionAsync(db, GetDataObject(doc), eventType, now, ct);

            default:
                // Unknown type: the inbox row is recorded Processed with no side effect.
                return null;
        }
    }

    private async Task<string> ProcessInvoiceAsync(
        RdDbContext db, JsonElement obj, string eventType, DateTimeOffset now, CancellationToken ct)
    {
        var invoiceId = Str(obj, "id")
            ?? throw new InvalidOperationException("Stripe invoice event has no invoice id.");
        var customerId = Str(obj, "customer") ?? "";
        // Basil API versions moved invoice.subscription under parent.subscription_details.
        var subscriptionId = Str(obj, "subscription") ?? NestedSubscription(obj);
        var status = Str(obj, "status") ?? "";
        var amountDue = MinorToDecimal(Long(obj, "amount_due")) ?? 0m;
        var amountPaid = MinorToDecimal(Long(obj, "amount_paid")) ?? 0m;
        var currency = (Str(obj, "currency") ?? "usd").ToUpperInvariant();
        var hostedUrl = Str(obj, "hosted_invoice_url");
        var created = FromUnix(Long(obj, "created")) ?? now;
        var dueDate = FromUnix(Long(obj, "due_date"));
        var paidAt = obj.TryGetProperty("status_transitions", out var transitions)
            && transitions.ValueKind == JsonValueKind.Object
                ? FromUnix(Long(transitions, "paid_at"))
                : null;

        var clientId = await ResolveClientAsync(db, subscriptionId, customerId, ct);

        var proj = await db.StripeInvoices.FirstOrDefaultAsync(p => p.InvoiceId == invoiceId, ct);
        if (proj is null)
        {
            proj = new StripeInvoiceProj { InvoiceId = invoiceId, CustomerId = customerId, Status = status };
            db.StripeInvoices.Add(proj);
        }
        proj.CustomerId = customerId;
        proj.SubscriptionId = subscriptionId;
        proj.Status = status;
        proj.AmountDue = amountDue;
        proj.AmountPaid = amountPaid;
        proj.CurrencyCode = currency;
        proj.HostedInvoiceUrl = hostedUrl;
        proj.CreatedAtSource = created;
        proj.DueDate = dueDate;
        proj.ClientId = clientId;
        proj.SourceSyncedAt = now;

        // Only a genuine payment writes money to the ledger. The idempotent insert
        // dedups against a ledger row the polling sweep may already have created for
        // this same invoice — money is never double-counted.
        if (eventType == "invoice.paid" && status == "paid" && amountPaid > 0m && clientId is { } cid)
        {
            var entry = new LedgerEntry
            {
                ClientId = cid,
                OccurredAt = paidAt ?? created,
                RecordedAt = now,
                Type = LedgerEntryType.ChargePaid,
                SignedAmount = amountPaid, // money in → positive
                CurrencyCode = currency,
                SourceSystem = ExternalSystem.Stripe,
                SourceObjectId = invoiceId,
            };
            await LedgerIngest.InsertIdempotentAsync(db, new[] { entry }, ct);

            // Convert→Bill→Close (rung B): the first payment for a conversion's subscription promotes it.
            // Correlates on the subscription id stamped at billing-execute. Idempotent: the AwaitingPayment
            // guard means a redelivery / poison-replay / renewal invoice never re-promotes (state has moved on).
            await PromoteConversionAsync(db, subscriptionId, now, ct);
        }

        return invoiceId;
    }

    /// <summary>
    /// First-payment promotion: move the conversion AwaitingPayment → Paid and its client's active trial
    /// Active → Promoted, so EligibilityPolicy stops suppressing enforcement (rule 4) with no drift window.
    /// Runs in the caller's transaction. (The `closed` GHL tag write is a later, spike-gated step.)
    /// </summary>
    private static async Task PromoteConversionAsync(RdDbContext db, string? subscriptionId, DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(subscriptionId)) return;

        var intent = await db.ConvertIntents.FirstOrDefaultAsync(
            i => i.StripeSubscriptionId == subscriptionId && i.State == ConvertIntentState.AwaitingPayment, ct);
        if (intent is null) return;

        intent.State = ConvertIntentState.Paid;
        intent.UpdatedAt = now;

        var trial = await db.TrialPeriods
            .Where(t => t.ClientId == intent.ClientId && t.Outcome == TrialOutcome.Active)
            .OrderByDescending(t => t.StartsAt)
            .FirstOrDefaultAsync(ct);
        if (trial is not null)
            trial.Outcome = TrialOutcome.Promoted;
    }

    private async Task<string> ProcessSubscriptionAsync(
        RdDbContext db, JsonElement obj, string eventType, DateTimeOffset now, CancellationToken ct)
    {
        var subscriptionId = Str(obj, "id")
            ?? throw new InvalidOperationException("Stripe subscription event has no subscription id.");
        var customerId = Str(obj, "customer") ?? "";
        var status = Str(obj, "status")
            ?? (eventType == "customer.subscription.deleted" ? "canceled" : "");
        var canceledAt = FromUnix(Long(obj, "canceled_at"));
        var (interval, amount, currency) = ParsePrice(obj);

        var clientId = await ResolveClientAsync(db, subscriptionId, customerId, ct);

        var proj = await db.StripeSubscriptions.FirstOrDefaultAsync(p => p.SubscriptionId == subscriptionId, ct);
        if (proj is null)
        {
            proj = new StripeSubscriptionProj { SubscriptionId = subscriptionId, CustomerId = customerId, Status = status };
            db.StripeSubscriptions.Add(proj);
        }
        proj.CustomerId = customerId;
        proj.Status = status;
        proj.CanceledAt = canceledAt;
        if (interval is not null) proj.PriceInterval = interval;
        if (amount is not null) proj.Amount = amount;
        if (currency is not null) proj.CurrencyCode = currency;
        proj.ClientId = clientId;
        proj.SourceSyncedAt = now;

        return subscriptionId;
    }

    /// <summary>
    /// Resolves ClientId from active identity links — subscription first, then
    /// customer. Invalidated links never resolve (drift must not route money).
    /// </summary>
    private static async Task<Guid?> ResolveClientAsync(
        RdDbContext db, string? subscriptionId, string customerId, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(subscriptionId))
        {
            var bySubscription = await db.IdentityLinks
                .Where(l => l.System == ExternalSystem.Stripe && l.Kind == LinkKind.Subscription
                            && l.ExternalId == subscriptionId && l.InvalidatedAt == null)
                .Select(l => (Guid?)l.ClientId)
                .FirstOrDefaultAsync(ct);
            if (bySubscription is not null) return bySubscription;
        }

        if (!string.IsNullOrEmpty(customerId))
        {
            return await db.IdentityLinks
                .Where(l => l.System == ExternalSystem.Stripe && l.Kind == LinkKind.Customer
                            && l.ExternalId == customerId && l.InvalidatedAt == null)
                .Select(l => (Guid?)l.ClientId)
                .FirstOrDefaultAsync(ct);
        }

        return null;
    }

    private static JsonElement GetDataObject(JsonDocument doc)
    {
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("object", out var obj) && obj.ValueKind == JsonValueKind.Object)
            return obj;
        throw new InvalidOperationException("Stripe event payload has no data.object.");
    }

    private static string? NestedSubscription(JsonElement invoice)
    {
        if (invoice.TryGetProperty("parent", out var parent) && parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty("subscription_details", out var details) && details.ValueKind == JsonValueKind.Object)
            return Str(details, "subscription");
        return null;
    }

    private static (string? Interval, decimal? Amount, string? Currency) ParsePrice(JsonElement subscription)
    {
        if (subscription.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object
            && items.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
            && data.GetArrayLength() > 0
            && data[0].TryGetProperty("price", out var price) && price.ValueKind == JsonValueKind.Object)
        {
            var interval = price.TryGetProperty("recurring", out var recurring) && recurring.ValueKind == JsonValueKind.Object
                ? Str(recurring, "interval")
                : null;
            var amount = MinorToDecimal(Long(price, "unit_amount"));
            var currency = Str(price, "currency")?.ToUpperInvariant();
            return (interval, amount, currency);
        }
        return (null, null, null);
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static long? Long(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var v)
            ? v
            : null;

    private static decimal? MinorToDecimal(long? minor) => minor is { } m ? m / 100m : null;

    private static DateTimeOffset? FromUnix(long? seconds) =>
        seconds is { } s ? DateTimeOffset.FromUnixTimeSeconds(s) : null;

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private static bool IsDuplicateKey(DbUpdateException ex)
    {
        for (Exception? inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is SqlException { Number: 2601 or 2627 })
                return true;
        }
        return false;
    }
}
