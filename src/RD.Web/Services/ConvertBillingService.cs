using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RD.Domain;
using RD.Domain.Entities;
using RD.Infrastructure;
using RD.Infrastructure.Enforcement;
using RD.Infrastructure.Gateways;
using RD.Infrastructure.Webhooks;

namespace RD.Web.Services;

/// <summary>
/// Human-approved Convert-to-Bill execution. The exact customer and price are
/// frozen before the first Stripe write. Stable per-intent idempotency keys, a
/// live cluster-wide subscription check, and a short optimistic retry lease let
/// an interrupted attempt resume without changing target or double-subscribing.
/// </summary>
public class ConvertBillingService(
    IDbContextFactory<RdDbContext> factory,
    IStripeGateway stripe,
    KillSwitchService killSwitch,
    IClock clock,
    Func<string, CancellationToken, Task>? beforeStripeWrite = null)
{
    private static readonly TimeSpan AwaitingPaymentWindow = TimeSpan.FromDays(7);
    private static readonly TimeSpan BillingAttemptLease = TimeSpan.FromMinutes(5);

    public async Task<ConvertResult> ExecuteAsync(
        Guid intentId,
        string? actor,
        string? expectedStripePriceId,
        string? expectedStripeCustomerId,
        bool expectedWouldCreateCustomer,
        CancellationToken ct = default)
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

        // Hold the same per-client fence used by mapping writes and enforcement
        // dispatch from the final draft calculation through the Stripe POST.
        // A newly linked customer/subscription or ownership investigation can
        // therefore never cross the last live cluster check.
        await using var mutationFence = await ClientMutationFence.AcquireAsync(db, client.Id, ct);
        await using var ownershipFence = await ClientMutationFence.AcquireMappingOwnershipAsync(db, ct);
        await db.Entry(intent).ReloadAsync(ct);
        if (intent.State != ConvertIntentState.Drafted)
            return new ConvertResult(false, $"Conversion is already {intent.State} — nothing to bill.");
        await db.Entry(client).ReloadAsync(ct);
        if (client.MergedIntoClientId is not null)
            return new ConvertResult(false,
                "This account was merged into another — billing is blocked on the retired account. Review the conversion on the surviving account.");

        var now = clock.UtcNow;
        var isResume = intent.BillingStartedAt is not null;
        ConvertDraft draft;
        if (!isResume)
        {
            var input = await ConvertService.LoadDraftInputAsync(
                db, client, intent.AccountType, intent.PackageId, now, ct);
            draft = ConvertDrafter.Draft(input);
            if (!draft.Ready)
                return new ConvertResult(false, $"Can't bill yet: {string.Join(" ", draft.Blockers)}");
        }
        else if (!ConvertService.TryReadDraft(intent.DraftedActionJson, out var frozenDraft)
                 || frozenDraft is null
                 || !frozenDraft.Ready)
        {
            return new ConvertResult(false,
                "The approved billing draft cannot be recovered safely. Do not retry in Stripe; ask an administrator to review this conversion.");
        }
        else
        {
            draft = frozenDraft;
        }

        if (!string.Equals(draft.StripePriceId, expectedStripePriceId, StringComparison.Ordinal)
            || !string.Equals(draft.StripeCustomerId, expectedStripeCustomerId, StringComparison.Ordinal)
            || draft.WouldCreateCustomer != expectedWouldCreateCustomer)
        {
            return new ConvertResult(false,
                "The billing target or price changed after it was displayed. Refresh the client and review the updated draft before approving.");
        }

        if (isResume && now - intent.UpdatedAt < BillingAttemptLease)
            return new ConvertResult(false,
                "Billing is already in progress. Wait a few minutes before retrying; Stripe idempotency keeps the attempt single.");

        if (!isResume)
        {
            intent.DraftedActionJson = JsonSerializer.Serialize(draft);
            intent.StripeCustomerId = draft.StripeCustomerId;
            intent.BillingStartedAt = now;
        }
        intent.UpdatedAt = now;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new ConvertResult(false,
                "Another billing attempt just started. No second Stripe write was made.");
        }

        try
        {
            // Re-read every Stripe mapping after claiming the intent and before
            // the first vendor write. A subscription mapping added after the
            // draft must never be hidden merely because its Customer link is
            // missing.
            var activeStripeLinks = await db.IdentityLinks.AsNoTracking()
                .Where(l => l.ClientId == client.Id
                            && l.System == ExternalSystem.Stripe
                            && l.InvalidatedAt == null
                            && (l.Kind == LinkKind.Customer || l.Kind == LinkKind.Subscription))
                .ToListAsync(ct);
            var activeCustomerIds = activeStripeLinks
                .Where(l => l.Kind == LinkKind.Customer)
                .Select(l => l.ExternalId)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var activeSubscriptionIds = activeStripeLinks
                .Where(l => l.Kind == LinkKind.Subscription)
                .Select(l => l.ExternalId)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (!isResume
                && draft.WouldCreateCustomer
                && (activeCustomerIds.Count > 0 || activeSubscriptionIds.Count > 0))
            {
                await ReleaseBillingClaimAsync(db, intent, ct);
                return new ConvertResult(false,
                    "Stripe mappings changed after the billing draft was displayed. The conversion was unlocked; refresh and review the linked accounts before billing.");
            }

            var customerId = isResume
                ? intent.StripeCustomerId ?? draft.StripeCustomerId
                : draft.StripeCustomerId;
            if (string.IsNullOrEmpty(customerId))
            {
                if (beforeStripeWrite is not null)
                    await beforeStripeWrite("customer", ct);
                if (await killSwitch.IsEngagedAsync(ct))
                {
                    if (!isResume) await ReleaseBillingClaimAsync(db, intent, ct);
                    return new ConvertResult(false,
                        "The global kill switch was engaged before the Stripe customer write. No Stripe customer or subscription was created.");
                }

                customerId = await stripe.CreateCustomerAsync(
                    client.ContactName ?? client.BusinessName,
                    client.Email,
                    $"convert-{intent.Id}-cust",
                    ct);
                await EnsureLinkAsync(db, client.Id, LinkKind.Customer, customerId, ct);
                intent.StripeCustomerId = customerId;
                await db.SaveChangesAsync(ct);
                if (!activeCustomerIds.Contains(customerId, StringComparer.Ordinal))
                    activeCustomerIds.Add(customerId);
            }

            var targetIsStillActive = activeCustomerIds.Contains(customerId, StringComparer.Ordinal);
            if (!targetIsStillActive && !isResume)
            {
                await ReleaseBillingClaimAsync(db, intent, ct);
                return new ConvertResult(false,
                    "The approved Stripe customer is no longer an active client link. The conversion was unlocked; refresh and review it again.");
            }

            var liveCustomerIds = activeCustomerIds.ToList();
            if (!targetIsStillActive)
                liveCustomerIds.Add(customerId); // recover the frozen target; never redirect a possible lost-ack write

            // Subscription links are independently required mappings. Use their
            // projections to discover customer accounts omitted from the active
            // Customer-link set, then verify those accounts live as well.
            if (activeSubscriptionIds.Count > 0)
            {
                var projectedOwners = await db.StripeSubscriptions.AsNoTracking()
                    .Where(s => activeSubscriptionIds.Contains(s.SubscriptionId))
                    .Select(s => s.CustomerId)
                    .Distinct()
                    .ToListAsync(ct);
                foreach (var projectedOwner in projectedOwners.Where(id => !string.IsNullOrWhiteSpace(id)))
                {
                    if (!liveCustomerIds.Contains(projectedOwner, StringComparer.Ordinal))
                        liveCustomerIds.Add(projectedOwner);
                }
            }

            // This read happens immediately before the POST. Unlike the projection
            // gate, it catches subscriptions created since the last sweep. On a
            // retry, convert_intent_id identifies a prior successful lost-ack write.
            var liveSubscriptions = new List<StripeSubscriptionDto>();
            foreach (var linkedCustomerId in liveCustomerIds)
                liveSubscriptions.AddRange(
                    await stripe.ListSubscriptionsForCustomerAsync(linkedCustomerId, ct));

            var uncoveredSubscriptionIds = activeSubscriptionIds
                .Where(subscriptionId => !liveSubscriptions.Any(s =>
                    string.Equals(s.Id, subscriptionId, StringComparison.Ordinal)))
                .ToList();
            var subscriptionsWithoutCustomerLinks = liveSubscriptions
                .Where(s => activeSubscriptionIds.Contains(s.Id, StringComparer.Ordinal)
                            && !activeCustomerIds.Contains(s.CustomerId, StringComparer.Ordinal))
                .Select(s => s.Id)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (uncoveredSubscriptionIds.Count > 0 || subscriptionsWithoutCustomerLinks.Count > 0)
            {
                if (!isResume) await ReleaseBillingClaimAsync(db, intent, ct);
                return new ConvertResult(false,
                    isResume
                        ? "A linked Stripe subscription cannot be safely matched to an active customer mapping. This billing attempt remains frozen; ask an administrator to correct the mapping."
                        : "A linked Stripe subscription cannot be safely matched to an active customer mapping. The conversion was unlocked; correct the mapping before billing.");
            }

            var intentKey = intent.Id.ToString();
            var recoveryMatches = liveSubscriptions
                .Where(s => string.Equals(s.ConvertIntentId, intentKey, StringComparison.Ordinal)
                            && string.Equals(s.CustomerId, customerId, StringComparison.Ordinal))
                .ToList();
            if (recoveryMatches.Count > 1)
            {
                return new ConvertResult(false,
                    "Multiple Stripe subscriptions claim this conversion. The approved attempt remains frozen; reconcile the duplicate subscriptions before retrying.");
            }

            var recovered = recoveryMatches.SingleOrDefault();
            if (recovered is not null
                && !string.Equals(recovered.PriceId, draft.StripePriceId, StringComparison.Ordinal))
            {
                return new ConvertResult(false,
                    "Stripe returned a subscription for this conversion, but its price does not exactly match the approved frozen price. The attempt remains frozen for reconciliation.");
            }
            var ownershipStillUnconfirmed = await db.InvestigationItems.AsNoTracking()
                .AnyAsync(i => i.ClientId == client.Id
                               && i.Kind == InvestigationKind.DuplicateStripeCustomer
                               && i.Status != InvestigationStatus.Resolved, ct);

            if (recovered is null && ownershipStillUnconfirmed)
            {
                if (!isResume) await ReleaseBillingClaimAsync(db, intent, ct);
                return new ConvertResult(false,
                    isResume
                        ? "This frozen billing attempt cannot continue while Stripe customer ownership is unconfirmed. It remains frozen for safe recovery; ask an administrator to review it."
                        : "Stripe customer ownership is not confirmed. The conversion was unlocked; resolve the multiple-customer investigation before billing.");
            }

            var conflictingLive = liveSubscriptions
                .Where(s => IsNonTerminal(s.Status)
                            && (recovered is null
                                || !string.Equals(s.Id, recovered.Id, StringComparison.Ordinal)))
                .ToList();
            if (conflictingLive.Count > 0)
            {
                if (recovered is null && !isResume)
                    await ReleaseBillingClaimAsync(db, intent, ct);
                return new ConvertResult(false,
                    recovered is not null
                        ? "A recovered subscription matches this conversion, but another live subscription exists in the linked customer cluster. The attempt remains frozen for reconciliation."
                        : isResume
                        ? "A linked Stripe customer now has another live subscription. This possible lost-ack attempt remains frozen; no new Stripe write was made. Ask an administrator to reconcile it."
                        : "A linked Stripe customer already has a live subscription. No subscription was created, and this conversion was unlocked for review.");
            }

            if (recovered is null && isResume && !targetIsStillActive)
                return new ConvertResult(false,
                    "The frozen Stripe customer is no longer an active mapping and no matching subscription was found. The attempt remains frozen; ask an administrator to reconcile it before any new billing.");

            if (recovered is not null && !IsNonTerminal(recovered.Status))
            {
                await EnsureLinkAsync(db, client.Id, LinkKind.Subscription, recovered.Id, ct);
                intent.StripeSubscriptionId = recovered.Id;
                intent.State = string.Equals(recovered.Status, "canceled", StringComparison.OrdinalIgnoreCase)
                    ? ConvertIntentState.Reversed
                    : ConvertIntentState.Failed;
                intent.ExpiresAt = null;
                intent.UpdatedAt = clock.UtcNow;
                await db.SaveChangesAsync(ct);
                return new ConvertResult(false,
                    $"Recovered subscription {recovered.Id}, but it is {recovered.Status}. The conversion was closed as {intent.State}; review Stripe before starting a new conversion.",
                    intent.Id);
            }

            StripeSubscriptionDto sub;
            if (recovered is not null)
            {
                sub = recovered;
            }
            else
            {
                if (beforeStripeWrite is not null)
                    await beforeStripeWrite("subscription", ct);
                if (await killSwitch.IsEngagedAsync(ct))
                {
                    return new ConvertResult(false,
                        "The global kill switch was engaged before the Stripe subscription write. No subscription was created; this approved attempt remains frozen for a safe retry.");
                }

                sub = await stripe.CreateSubscriptionAsync(
                    customerId,
                    draft.StripePriceId!,
                    new Dictionary<string, string> { ["convert_intent_id"] = intentKey },
                    $"convert-{intent.Id}",
                    ct);
            }

            await EnsureLinkAsync(db, client.Id, LinkKind.Subscription, sub.Id, ct);

            var completedAt = clock.UtcNow;
            intent.State = ConvertIntentState.AwaitingPayment;
            intent.StripeCustomerId = customerId;
            intent.StripeSubscriptionId = sub.Id;
            intent.ExpiresAt = completedAt + AwaitingPaymentWindow;
            intent.UpdatedAt = completedAt;

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                await using var check = await factory.CreateDbContextAsync(ct);
                var current = await check.ConvertIntents.AsNoTracking()
                    .FirstOrDefaultAsync(i => i.Id == intent.Id, ct);
                if (current?.StripeSubscriptionId == sub.Id
                    || current?.State is ConvertIntentState.AwaitingPayment
                        or ConvertIntentState.Paid
                        or ConvertIntentState.Closed)
                {
                    return new ConvertResult(true,
                        $"Subscription {sub.Id} was already recorded by another action.", intent.Id);
                }

                return new ConvertResult(false,
                    "Stripe returned the subscription, but the conversion changed concurrently. Retry after the current attempt clears; the Stripe request is idempotent.");
            }

            var paidReconciliation = await ReconcilePaidProjectionAsync(
                intent.Id, client.Id, customerId, sub.Id, ct);
            if (paidReconciliation is null)
                return new ConvertResult(false,
                    $"Subscription {sub.Id} was created, but its recorded payment could not be reconciled after a concurrent change. No second subscription will be created; ask an administrator to retry payment reconciliation.",
                    intent.Id);

            return new ConvertResult(
                true,
                paidReconciliation == true
                    ? $"Subscription {sub.Id} created and first payment recorded."
                    : $"Subscription {sub.Id} created — awaiting first payment.",
                intent.Id);
        }
        catch (HttpRequestException ex)
        {
            if (!isResume && IsDefiniteClientError(ex.StatusCode))
            {
                await ReleaseBillingClaimAsync(db, intent, ct);
                return new ConvertResult(false,
                    "Stripe rejected the request before a subscription was created. The conversion was unlocked; correct the Stripe configuration or billing data, then refresh and try again.");
            }

            return new ConvertResult(false,
                "Stripe did not confirm the billing attempt. Wait five minutes, then retry this same conversion; its customer, price, and idempotency key are frozen.");
        }
    }

    /// <summary>
    /// Closes the narrow race where Stripe's paid-invoice webhook is committed
    /// before the subscription id is persisted on the conversion. A fresh
    /// context on each attempt prevents a concurrency exception from being
    /// mistaken for proof that the webhook completed the promotion.
    /// </summary>
    private async Task<bool?> ReconcilePaidProjectionAsync(
        Guid intentId,
        Guid clientId,
        string customerId,
        string subscriptionId,
        CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var reconciliationDb = await factory.CreateDbContextAsync(ct);
            var hasPaidInvoice = await reconciliationDb.StripeInvoices.AsNoTracking()
                .AnyAsync(i => i.SubscriptionId == subscriptionId
                               && i.CustomerId == customerId
                               && i.Status == "paid"
                               && i.AmountPaid > 0, ct);
            if (!hasPaidInvoice) return false;

            await StripeWebhookIngestor.PromoteConversionAsync(
                reconciliationDb,
                subscriptionId,
                customerId,
                intentId,
                clientId,
                clock.UtcNow,
                ct);

            try
            {
                await reconciliationDb.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                if (await ConversionHasRecordedPaymentAsync(intentId, ct)) return true;
                continue;
            }

            if (await ConversionHasRecordedPaymentAsync(intentId, ct)) return true;
        }

        return await ConversionHasRecordedPaymentAsync(intentId, ct) ? true : null;
    }

    private async Task<bool> ConversionHasRecordedPaymentAsync(Guid intentId, CancellationToken ct)
    {
        await using var check = await factory.CreateDbContextAsync(ct);
        return await check.ConvertIntents.AsNoTracking()
            .AnyAsync(i => i.Id == intentId
                           && (i.State == ConvertIntentState.Paid
                               || i.State == ConvertIntentState.Closed), ct);
    }

    public async Task<ConvertResult> CancelAsync(
        Guid intentId, string? actor = null, CancellationToken ct = default)
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

        return new ConvertResult(true,
            $"Subscription {intent.StripeSubscriptionId} canceled — conversion reversed.", intent.Id);
    }

    private static bool IsNonTerminal(string? status) =>
        !string.Equals(status, "canceled", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(status, "incomplete_expired", StringComparison.OrdinalIgnoreCase);

    private static bool IsDefiniteClientError(HttpStatusCode? status) =>
        status is >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError
        && status is not HttpStatusCode.RequestTimeout
        && status is not HttpStatusCode.Conflict
        && status is not HttpStatusCode.TooManyRequests;

    private async Task ReleaseBillingClaimAsync(
        RdDbContext db, ConvertIntent intent, CancellationToken ct)
    {
        intent.BillingStartedAt = null;
        intent.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private IdentityLink NewLink(Guid clientId, LinkKind kind, string externalId) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        System = ExternalSystem.Stripe,
        Kind = kind,
        ExternalId = externalId,
        VerifiedAt = clock.UtcNow,
        CreatedAt = clock.UtcNow,
    };

    private async Task EnsureLinkAsync(
        RdDbContext db, Guid clientId, LinkKind kind, string externalId, CancellationToken ct)
    {
        if (await LinkExistsAsync(db, clientId, kind, externalId, ct)) return;

        var link = NewLink(clientId, kind, externalId);
        db.IdentityLinks.Add(link);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.Entry(link).State = EntityState.Detached;
            if (!await LinkExistsAsync(db, clientId, kind, externalId, ct)) throw;
        }
    }

    private static Task<bool> LinkExistsAsync(
        RdDbContext db, Guid clientId, LinkKind kind, string externalId, CancellationToken ct) =>
        db.IdentityLinks.AnyAsync(l => l.ClientId == clientId
                                       && l.System == ExternalSystem.Stripe
                                       && l.Kind == kind
                                       && l.ExternalId == externalId
                                       && l.InvalidatedAt == null, ct);
}
