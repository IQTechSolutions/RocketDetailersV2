using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RD.Domain;
using RD.Domain.Entities;
using RD.Infrastructure.Gateways;

namespace RD.Infrastructure.Sync;

/// <summary>
/// Full-sweep Stripe sync: upserts subscription + invoice projections and
/// ingests paid/failed charges into the append-only ledger. Wrapped in a
/// SyncRun that only goes green after the COMPLETE sweep — a partially
/// paginated sync never looks Completed. Exceptions are recorded and
/// swallowed; the Hangfire schedule is the retry authority.
/// </summary>
public sealed class StripeSyncJob(
    IDbContextFactory<RdDbContext> dbFactory,
    IStripeGateway stripe,
    IClock clock,
    ILogger<StripeSyncJob> logger)
{
    /// <summary>Invoice sweep window; OPEN invoices are always swept regardless of age.</summary>
    public static readonly TimeSpan RecentInvoiceWindow = TimeSpan.FromDays(30);

    public async Task RunAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var run = new SyncRun { System = ExternalSystem.Stripe, StartedAt = clock.UtcNow };
        db.SyncRuns.Add(run);
        await db.SaveChangesAsync(ct);

        try
        {
            run.ItemsSeen = await SweepAsync(db, ct);
            run.Status = SyncRunStatus.Completed;
            run.CompletedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Stripe sync sweep failed; SyncRun {SyncRunId} marked Failed", run.Id);
            await SyncUtil.RecordFailureAsync(db, run, ex, logger);
        }
    }

    private async Task<int> SweepAsync(RdDbContext db, CancellationToken ct)
    {
        var now = clock.UtcNow;

        // Active identity links resolve vendor ids → ClientId. Invalidated links
        // never resolve (drift demotes; a stale mapping must not route money).
        var links = await db.IdentityLinks
            .Where(l => l.System == ExternalSystem.Stripe && l.InvalidatedAt == null
                        && (l.Kind == LinkKind.Customer || l.Kind == LinkKind.Subscription))
            .Select(l => new { l.Kind, l.ExternalId, l.ClientId })
            .ToListAsync(ct);
        var customerToClient = links.Where(l => l.Kind == LinkKind.Customer)
            .ToDictionary(l => l.ExternalId, l => l.ClientId);
        var subscriptionToClient = links.Where(l => l.Kind == LinkKind.Subscription)
            .ToDictionary(l => l.ExternalId, l => l.ClientId);

        Guid? Resolve(string? subscriptionId, string customerId)
        {
            if (subscriptionId is not null && subscriptionToClient.TryGetValue(subscriptionId, out var bySub))
                return bySub;
            return customerToClient.TryGetValue(customerId, out var byCustomer) ? byCustomer : null;
        }

        // --- Subscriptions: complete paginated sweep, then upsert.
        var subscriptions = await stripe.ListSubscriptionsAsync(ct);
        var subProjections = await db.StripeSubscriptions.ToDictionaryAsync(p => p.SubscriptionId, ct);
        foreach (var sub in subscriptions)
        {
            if (!subProjections.TryGetValue(sub.Id, out var proj))
            {
                proj = new StripeSubscriptionProj { SubscriptionId = sub.Id, CustomerId = sub.CustomerId, Status = sub.Status };
                subProjections[sub.Id] = proj;
                db.StripeSubscriptions.Add(proj);
            }
            proj.CustomerId = sub.CustomerId;
            proj.Status = sub.Status;
            proj.PriceInterval = sub.PriceInterval;
            proj.Amount = sub.Amount;
            proj.CurrencyCode = sub.CurrencyCode;
            proj.CanceledAt = sub.CanceledAt;
            proj.ClientId = Resolve(sub.Id, sub.CustomerId);
            proj.SourceSyncedAt = now;
        }

        // --- Invoices: ALL open ones (age-independent — they gate eligibility)
        // plus a recent window (catches paid / uncollectible transitions).
        var openInvoices = await stripe.ListInvoicesAsync("open", null, ct);
        var recentInvoices = await stripe.ListInvoicesAsync(null, now - RecentInvoiceWindow, ct);
        var invoices = openInvoices.Concat(recentInvoices)
            .GroupBy(i => i.Id)
            .Select(g => g.First())
            .ToList();

        var invoiceProjections = await db.StripeInvoices.ToDictionaryAsync(p => p.InvoiceId, ct);
        foreach (var invoice in invoices)
        {
            if (!invoiceProjections.TryGetValue(invoice.Id, out var proj))
            {
                proj = new StripeInvoiceProj { InvoiceId = invoice.Id, CustomerId = invoice.CustomerId, Status = invoice.Status };
                invoiceProjections[invoice.Id] = proj;
                db.StripeInvoices.Add(proj);
            }
            proj.CustomerId = invoice.CustomerId;
            proj.SubscriptionId = invoice.SubscriptionId;
            proj.Status = invoice.Status;
            proj.AmountDue = invoice.AmountDue;
            proj.AmountPaid = invoice.AmountPaid;
            proj.CurrencyCode = invoice.CurrencyCode;
            proj.HostedInvoiceUrl = invoice.HostedInvoiceUrl;
            proj.CreatedAtSource = invoice.Created;
            proj.DueDate = invoice.DueDate;
            proj.ClientId = Resolve(invoice.SubscriptionId, invoice.CustomerId);
            proj.SourceSyncedAt = now;
        }

        await db.SaveChangesAsync(ct);

        // --- Ledger ingestion (append-only, idempotent by (Source, ObjectId, Type)).
        var candidates = new List<LedgerEntry>();
        foreach (var invoice in invoices)
        {
            var clientId = Resolve(invoice.SubscriptionId, invoice.CustomerId);
            if (clientId is null) continue; // unmapped money is a work-queue problem, not a ledger row

            if (invoice.Status == "paid" && invoice.AmountPaid > 0)
            {
                candidates.Add(new LedgerEntry
                {
                    ClientId = clientId.Value,
                    OccurredAt = invoice.PaidAt ?? invoice.Created,
                    RecordedAt = now,
                    Type = LedgerEntryType.ChargePaid,
                    SignedAmount = invoice.AmountPaid, // money in → positive
                    CurrencyCode = invoice.CurrencyCode,
                    SourceSystem = ExternalSystem.Stripe,
                    SourceObjectId = invoice.Id,
                });
            }
            else if (IsFailed(invoice, now))
            {
                // ChargeFailed is an EVENT record: no money moved, so SignedAmount = 0.
                // The financial effect of a failure is the ABSENCE of the ChargePaid
                // entry — recording -amount_due here would double-count the moment
                // the invoice is later paid or voided. Exposure math reads spend vs
                // paid charges; this row exists to timestamp the failure for policy.
                candidates.Add(new LedgerEntry
                {
                    ClientId = clientId.Value,
                    OccurredAt = invoice.DueDate ?? invoice.Created,
                    RecordedAt = now,
                    Type = LedgerEntryType.ChargeFailed,
                    SignedAmount = 0m,
                    CurrencyCode = invoice.CurrencyCode,
                    SourceSystem = ExternalSystem.Stripe,
                    SourceObjectId = invoice.Id,
                });
            }
        }

        await LedgerIngest.InsertIdempotentAsync(db, candidates, ct);
        return subscriptions.Count + invoices.Count;
    }

    private static bool IsFailed(StripeInvoiceDto invoice, DateTimeOffset now) =>
        invoice.Status == "uncollectible"
        || (invoice.Status == "open" && invoice.DueDate is { } due && due < now);
}
