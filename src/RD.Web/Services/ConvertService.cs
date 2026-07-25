using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RD.Domain;
using RD.Domain.Entities;
using RD.Infrastructure;

namespace RD.Web.Services;

/// <summary>A package the operator can pick on the Convert dialog.</summary>
public sealed record PackageOption(Guid Id, string Name);

/// <summary>Result of creating a ConvertIntent — success flag, a human sentence for the snackbar, and the new id.</summary>
public sealed record ConvertResult(bool Ok, string Message, Guid? IntentId = null);

/// <summary>A client's active conversion and its freshly-computed draft, for the pending-conversion panel.</summary>
public sealed record ActiveConvertDraft(Guid IntentId, ConvertIntentState State, DateTimeOffset CreatedAt, ConvertDraft Draft);

/// <summary>
/// Operator writes for the Convert→Bill→Close wedge. A0 (this shell) does one thing:
/// when a closer clicks "Convert to subscriber", record the human intent as a
/// <see cref="ConvertIntent"/> in <see cref="ConvertIntentState.Drafted"/> and write the
/// chosen account type through to <see cref="Client.AccountType"/> explicitly.
///
/// The billing draft (A1), Stripe execution + `closed` write (B), and Auto promotion (C)
/// are NOT here. This never touches Stripe or GHL.
/// </summary>
public class ConvertService(IDbContextFactory<RdDbContext> factory, IClock clock)
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

        // Write the account type through explicitly — never rely on the enum default.
        client.AccountType = accountType;
        var resolvedPackageId = packageId ?? client.PackageId;

        // A1 (Shadow): compute the Stripe action this conversion WOULD take — no Stripe calls —
        // and record it on the intent as the reconciliation snapshot.
        var draftInput = await LoadDraftInputAsync(db, client, accountType, resolvedPackageId, ct);
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
        if (client is null) return null;

        var intent = await db.ConvertIntents.AsNoTracking()
            .Where(i => i.ClientId == clientId
                        && i.State != ConvertIntentState.Closed
                        && i.State != ConvertIntentState.Expired
                        && i.State != ConvertIntentState.Failed
                        && i.State != ConvertIntentState.Reversed)
            .OrderByDescending(i => i.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (intent is null) return null;

        var input = await LoadDraftInputAsync(db, client, intent.AccountType, intent.PackageId, ct);
        return new ActiveConvertDraft(intent.Id, intent.State, intent.CreatedAt, ConvertDrafter.Draft(input));
    }

    /// <summary>Snapshot the draft inputs from SQL projections for the pure <see cref="ConvertDrafter"/>.</summary>
    private static async Task<ConvertDraftInput> LoadDraftInputAsync(
        RdDbContext db, Client client, AccountType accountType, Guid? packageId, CancellationToken ct)
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

        // First customer in the client's Stripe cluster — deterministic order so the choice is reproducible.
        var firstCustomer = await db.IdentityLinks.AsNoTracking()
            .Where(l => l.ClientId == client.Id && l.System == ExternalSystem.Stripe
                        && l.Kind == LinkKind.Customer && l.InvalidatedAt == null)
            .OrderBy(l => l.CreatedAt).ThenBy(l => l.ExternalId)
            .Select(l => l.ExternalId)
            .FirstOrDefaultAsync(ct);

        return new ConvertDraftInput(accountType, client.CurrencyCode, packageId is not null, priceId, firstCustomer);
    }
}
