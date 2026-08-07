using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    IOptions<StripeOptions> options,
    IClock clock,
    ILogger<StripeSyncJob> logger)
{

    // Manual and recurring sweeps share this SQL-backed Hangfire lock. Without
    // it, overlapping upserts can both observe a missing projection and race to
    // insert the same primary key.
    [DisableConcurrentExecution("rd:stripe-sync", 30 * 60)]
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
        // Ledger sweep window (open invoices are always swept regardless of age).
        // Configurable so a one-off backfill can pull full history.
        var window = TimeSpan.FromDays(Math.Max(1, options.Value.LedgerLookbackDays));

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
        var recentInvoices = await stripe.ListInvoicesAsync(null, now - window, ct);
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

        // Money IN comes from CHARGES — the authoritative settled-money events. This
        // captures BOTH invoiced subscription payments AND direct one-off charges
        // that carry no invoice (which the invoice sweep can never see). The source
        // is tagged in SourceObjectId: an invoice id (in_…) ⇒ subscription payment,
        // a charge id (ch_…) ⇒ a direct payment — so analytics can split the two.
        var charges = await stripe.ListChargesAsync(now - window, ct);
        foreach (var charge in charges)
        {
            if (!charge.Paid || charge.Status != "succeeded") continue;
            var net = charge.Amount - charge.AmountRefunded; // net of refunds
            if (net <= 0m) continue;
            if (charge.CustomerId is null || !customerToClient.TryGetValue(charge.CustomerId, out var chargeClient)) continue;

            candidates.Add(new LedgerEntry
            {
                ClientId = chargeClient,
                OccurredAt = charge.Created,
                RecordedAt = now,
                Type = LedgerEntryType.ChargePaid,
                SignedAmount = net, // money in → positive
                CurrencyCode = charge.CurrencyCode,
                SourceSystem = ExternalSystem.Stripe,
                SourceObjectId = charge.InvoiceId ?? charge.Id, // in_… = subscription; ch_… = direct
            });
        }

        // Failure markers stay invoice-driven (past-due / uncollectible is the
        // eligibility signal). A ChargeFailed is a $0 event row (see below).
        foreach (var invoice in invoices)
        {
            var clientId = Resolve(invoice.SubscriptionId, invoice.CustomerId);
            if (clientId is null) continue; // unmapped money is a work-queue problem, not a ledger row

            if (IsFailed(invoice, now))
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
        return subscriptions.Count + invoices.Count + charges.Count;
    }

    private static bool IsFailed(StripeInvoiceDto invoice, DateTimeOffset now) =>
        invoice.Status == "uncollectible"
        || (invoice.Status == "open" && invoice.DueDate is { } due && due < now);
}
