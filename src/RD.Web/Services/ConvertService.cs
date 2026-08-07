using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RD.Domain;
using RD.Domain.Entities;
using RD.Domain.Policy;
using RD.Infrastructure;
using RD.Infrastructure.Enforcement;

namespace RD.Web.Services;

/// <summary>A package the operator can pick on the Convert dialog.</summary>
public sealed record PackageOption(Guid Id, string Name);

/// <summary>Result of creating a ConvertIntent — success flag, a human sentence for the snackbar, and the new id.</summary>
public sealed record ConvertResult(bool Ok, string Message, Guid? IntentId = null);

/// <summary>A client's active conversion and its freshly-computed draft, for the pending-conversion panel.</summary>
public sealed record ActiveConvertDraft(Guid IntentId, ConvertIntentState State, DateTimeOffset CreatedAt, ConvertDraft Draft);

/// <summary>One row of the conversions queue — every conversion in flight, plus why it might be stuck.</summary>
public sealed record ConversionRow(
    Guid IntentId, Guid ClientId, string BusinessName, ConvertIntentState State, AccountType AccountType,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? ExpiresAt,
    string? StripeSubscriptionId, bool HasGhlContact, bool CloseTagWritten)
{
    /// <summary>Non-null when this conversion needs a human to look at it, saying what's wrong.</summary>
    public string? Attention => State switch
    {
        ConvertIntentState.Expired => "Billed but never paid — expired.",
        ConvertIntentState.Paid when !HasGhlContact =>
            "Paid but no GHL contact linked — onboarding cannot fire. Link the contact.",
        ConvertIntentState.Paid when !CloseTagWritten =>
            "Paid — waiting on the `close` tag write (runs every 5 min).",
        ConvertIntentState.AwaitingPayment when ExpiresAt is { } e && e <= DateTimeOffset.UtcNow =>
            "Payment window has lapsed — the next sweep will expire it.",
        _ => null,
    };
}

/// <summary>
/// Operator writes for the Convert→Bill→Close wedge. A0 (this shell) does one thing:
/// when a closer clicks "Convert to subscriber", record the human intent as a
/// <see cref="ConvertIntent"/> in <see cref="ConvertIntentState.Drafted"/> and write the
/// chosen account type through to <see cref="Client.AccountType"/> explicitly.
///
/// The billing draft (A1), Stripe execution + `close` GHL tag write (B), and Auto promotion (C)
/// are NOT here. This never touches Stripe or GHL.
/// </summary>
public class ConvertService(
    IDbContextFactory<RdDbContext> factory,
    IClock clock,
    Func<Guid, CancellationToken, Task>? afterClientFence = null)
{
    private const string DefaultActor = "operator";

    /// <summary>Active packages for the Convert dialog's package picker.</summary>
    public async Task<IReadOnlyList<PackageOption>> GetActivePackagesAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Packages.AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.Name)
            .Select(p => new PackageOption(p.Id, p.Name))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Record the intent to convert a trial client to a subscriber. Guards: the client
    /// must exist, be live (not a retired duplicate), and still be a Trial; and there
    /// must be no active conversion already in flight. Writes Client.AccountType through
    /// explicitly so a later evaluation never falls back to the Master default.
    /// </summary>
    public async Task<ConvertResult> CreateIntentAsync(
        Guid clientId, AccountType accountType, Guid? packageId, string? actor = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        // Serialize intent creation with merge/unmerge and billing. Without this
        // fence a merge could prove "no conversion", retire the client, and then
        // lose a racing Drafted insert onto the retired shell.
        await using var mutationFence = await ClientMutationFence.AcquireAsync(db, clientId, ct);
        if (afterClientFence is not null)
            await afterClientFence(clientId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = clock.UtcNow;

        var client = await db.Clients.FirstOrDefaultAsync(c => c.Id == clientId, ct);
        if (client is null)
        {
            await tx.RollbackAsync(ct);
            return new ConvertResult(false, "Client not found (maybe it changed in another tab).");
        }
        if (client.MergedIntoClientId is not null)
        {
            await tx.RollbackAsync(ct);
            return new ConvertResult(false, "This account was merged into another — convert the surviving account instead.");
        }
        if (client.ContractType != ContractType.Trial)
        {
            await tx.RollbackAsync(ct);
            return new ConvertResult(false, "This client is already a subscriber — nothing to convert.");
        }

        var activeExists = await db.ConvertIntents.AnyAsync(
            i => i.ClientId == clientId
                 && i.State != ConvertIntentState.Closed
                 && i.State != ConvertIntentState.Expired
                 && i.State != ConvertIntentState.Failed
                 && i.State != ConvertIntentState.Reversed, ct);
        if (activeExists)
        {
            await tx.RollbackAsync(ct);
            return new ConvertResult(false, "A conversion is already in progress for this client.");
        }

        // Double-billing guard, independent of ContractType. A completed conversion means this client
        // already has a live subscription; converting again would create a SECOND one (a different
        // idempotency key, so Stripe won't collapse it). Catches clients converted before ContractType
        // was flipped on promotion, and any later data drift.
        var alreadyConverted = await db.ConvertIntents.AnyAsync(
            i => i.ClientId == clientId
                 && (i.State == ConvertIntentState.Paid || i.State == ConvertIntentState.Closed), ct);
        if (alreadyConverted)
        {
            await tx.RollbackAsync(ct);
            return new ConvertResult(false,
                "This client has already been converted and billed. Cancel the existing subscription first if you need to re-subscribe them.");
        }

        // Write the account type through explicitly — never rely on the enum default.
        client.AccountType = accountType;
        var resolvedPackageId = packageId ?? client.PackageId;

        // A1 (Shadow): compute the Stripe action this conversion WOULD take — no Stripe calls —
        // and record it on the intent as the reconciliation snapshot.
        var draftInput = await LoadDraftInputAsync(db, client, accountType, resolvedPackageId, now, ct);
        var draft = ConvertDrafter.Draft(draftInput);

        var intent = new ConvertIntent
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            AccountType = accountType,
            PackageId = resolvedPackageId,
            StripeCustomerId = draft.StripeCustomerId,
            State = ConvertIntentState.Drafted,
            DraftedActionJson = JsonSerializer.Serialize(draft),
            CreatedByUserId = string.IsNullOrWhiteSpace(actor) ? DefaultActor : actor,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.ConvertIntents.Add(intent);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return new ConvertResult(true,
            draft.Ready
                ? "Conversion started — draft ready for review."
                : "Conversion started — the draft has blockers to resolve (see the pending-conversion panel).",
            intent.Id);
    }

    /// <summary>
    /// The client's active conversion (if any) with its draft recomputed against current data — so
    /// setting a package price after Convert is reflected immediately. Read-only; computes nothing
    /// in Stripe. Null when there is no active conversion.
    /// </summary>
    public async Task<ActiveConvertDraft?> GetActiveDraftAsync(Guid clientId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var client = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId, ct);
        if (client is null || client.MergedIntoClientId is not null) return null;

        var intent = await db.ConvertIntents.AsNoTracking()
            .Where(i => i.ClientId == clientId
                        && i.State != ConvertIntentState.Closed
                        && i.State != ConvertIntentState.Expired
                        && i.State != ConvertIntentState.Failed
                        && i.State != ConvertIntentState.Reversed)
            .OrderByDescending(i => i.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (intent is null) return null;

        if (intent.BillingStartedAt is not null && TryReadDraft(intent.DraftedActionJson, out var frozenDraft))
            return new ActiveConvertDraft(intent.Id, intent.State, intent.CreatedAt, frozenDraft!);

        var input = await LoadDraftInputAsync(
            db, client, intent.AccountType, intent.PackageId, clock.UtcNow, ct);
        return new ActiveConvertDraft(intent.Id, intent.State, intent.CreatedAt, ConvertDrafter.Draft(input));
    }

    /// <summary>
    /// Every conversion, newest first — the operator queue. Includes terminal ones so history is
    /// visible; <see cref="ConversionRow.Attention"/> flags the ones actually needing a human
    /// (expired unpaid, paid-but-no-GHL-contact, paid-awaiting-tag).
    /// </summary>
    public async Task<List<ConversionRow>> ListConversionsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        return await db.ConvertIntents.AsNoTracking()
            .OrderByDescending(i => i.UpdatedAt)
            .Select(i => new ConversionRow(
                i.Id,
                i.ClientId,
                i.Client!.BusinessName,
                i.State,
                i.AccountType,
                i.CreatedAt,
                i.UpdatedAt,
                i.ExpiresAt,
                i.StripeSubscriptionId,
                db.IdentityLinks.Any(l => l.ClientId == i.ClientId && l.System == ExternalSystem.Ghl
                                          && l.Kind == LinkKind.Contact && l.InvalidatedAt == null),
                i.CloseTagWrittenAt != null))
            .ToListAsync(ct);
    }

    /// <summary>Snapshot the draft inputs from SQL projections for the pure <see cref="ConvertDrafter"/>. Shared with billing execute.</summary>
    internal static async Task<ConvertDraftInput> LoadDraftInputAsync(
        RdDbContext db, Client client, AccountType accountType, Guid? packageId,
        DateTimeOffset now, CancellationToken ct)
    {
        string? priceId = null;
        if (packageId is { } pid)
        {
            // The current effective version's Stripe price (most recent by EffectiveFrom).
            priceId = await db.PackageVersions.AsNoTracking()
                .Where(v => v.PackageId == pid)
                .OrderByDescending(v => v.EffectiveFrom)
                .Select(v => v.StripePriceId)
                .FirstOrDefaultAsync(ct);
        }

        // A multi-customer business must have an explicit preferred billing
        // customer. Never choose an arbitrary first row: that could create a
        // subscription on the wrong Stripe account. A single linked customer is
        // unambiguous and remains the safe fallback.
        var activeCustomers = await db.IdentityLinks.AsNoTracking()
            .Where(l => l.ClientId == client.Id && l.System == ExternalSystem.Stripe
                        && l.Kind == LinkKind.Customer && l.InvalidatedAt == null)
            .Select(l => l.ExternalId)
            .OrderBy(id => id)
            .ToListAsync(ct);
        var activeSubscriptionIds = await db.IdentityLinks.AsNoTracking()
            .Where(l => l.ClientId == client.Id && l.System == ExternalSystem.Stripe
                        && l.Kind == LinkKind.Subscription && l.InvalidatedAt == null)
            .Select(l => l.ExternalId)
            .Distinct()
            .ToListAsync(ct);

        var preferredCustomer = client.PreferredStripeCustomerId is { Length: > 0 } preferred
                                && activeCustomers.Contains(preferred, StringComparer.Ordinal)
            ? preferred
            : activeCustomers.Count == 1
                ? activeCustomers[0]
                : null;
        var ambiguousCustomers = activeCustomers.Count > 1 && preferredCustomer is null;
        var hasOpenOwnershipInvestigation = await db.InvestigationItems.AsNoTracking()
            .AnyAsync(i => i.ClientId == client.Id
                           && i.Kind == InvestigationKind.DuplicateStripeCustomer
                           && i.Status != InvestigationStatus.Resolved, ct);
        var latestStripeRun = await db.SyncRuns.AsNoTracking()
            .Where(r => r.System == ExternalSystem.Stripe
                        && r.Status == SyncRunStatus.Completed
                        && r.CompletedAt != null
                        && r.StartedAt <= now
                        && r.CompletedAt <= now)
            .OrderByDescending(r => r.CompletedAt)
            .FirstOrDefaultAsync(ct);
        var stripeEvidenceIsFresh = latestStripeRun?.CompletedAt is { } completedAt
                                    && now - completedAt <= EligibilityPolicy.StalenessBound;

        // Scan the whole linked customer cluster. A preference chooses the target
        // for a new subscription; it does not erase a subscription on another
        // confirmed account.
        var hasExistingNonTerminalSubscription = false;
        var hasSubscriptionWithoutCustomerLink = false;
        if (stripeEvidenceIsFresh && (activeCustomers.Count > 0 || activeSubscriptionIds.Count > 0))
        {
            var linkedSubscriptions = await db.StripeSubscriptions.AsNoTracking()
                .Where(s => (activeCustomers.Contains(s.CustomerId)
                             || activeSubscriptionIds.Contains(s.SubscriptionId))
                            && s.SourceSyncedAt >= latestStripeRun!.StartedAt
                            && s.SourceSyncedAt <= now)
                .ToListAsync(ct);

            hasSubscriptionWithoutCustomerLink = activeSubscriptionIds.Any(subscriptionId =>
            {
                var projection = linkedSubscriptions.FirstOrDefault(s => s.SubscriptionId == subscriptionId);
                return projection is null
                       || !activeCustomers.Contains(projection.CustomerId, StringComparer.Ordinal);
            });
            hasExistingNonTerminalSubscription = linkedSubscriptions.Any(subscription =>
                !string.Equals(subscription.Status, "canceled", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(subscription.Status, "incomplete_expired", StringComparison.OrdinalIgnoreCase));
        }

        return new ConvertDraftInput(
            accountType, client.CurrencyCode, packageId is not null, priceId,
            preferredCustomer, ambiguousCustomers, hasOpenOwnershipInvestigation,
            hasExistingNonTerminalSubscription, hasSubscriptionWithoutCustomerLink,
            stripeEvidenceIsFresh);
    }

    internal static bool TryReadDraft(string? json, out ConvertDraft? draft)
    {
        draft = null;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            draft = JsonSerializer.Deserialize<ConvertDraft>(json);
            return draft is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
