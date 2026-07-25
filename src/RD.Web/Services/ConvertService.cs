using Microsoft.EntityFrameworkCore;
using RD.Domain;
using RD.Domain.Entities;
using RD.Infrastructure;

namespace RD.Web.Services;

/// <summary>A package the operator can pick on the Convert dialog.</summary>
public sealed record PackageOption(Guid Id, string Name);

/// <summary>Result of creating a ConvertIntent — success flag, a human sentence for the snackbar, and the new id.</summary>
public sealed record ConvertResult(bool Ok, string Message, Guid? IntentId = null);

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

        var intent = new ConvertIntent
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            AccountType = accountType,
            PackageId = packageId ?? client.PackageId,
            State = ConvertIntentState.Drafted,
            CreatedByUserId = string.IsNullOrWhiteSpace(actor) ? DefaultActor : actor,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.ConvertIntents.Add(intent);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return new ConvertResult(true, "Conversion started — drafting the subscription next.", intent.Id);
    }
}
