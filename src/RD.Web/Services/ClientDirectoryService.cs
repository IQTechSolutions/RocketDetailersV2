using Microsoft.EntityFrameworkCore;
using RD.Domain;
using RD.Domain.Entities;
using RD.Infrastructure;

namespace RD.Web.Services;

/// <summary>One row of the clients grid with link-health flags (present + not invalidated).</summary>
public sealed record ClientListRow(
    Guid Id,
    string BusinessName,
    string? ContactName,
    ContractType ContractType,
    AccountType AccountType,
    EnforcementMode Mode,
    string? Country,
    string CurrencyCode,
    bool HasStripeCustomer,
    bool HasStripeSubscription,
    bool HasMetaCampaign,
    bool HasGhlContact)
{
    /// <summary>Missing any of the three enforcement-critical links (Stripe sub, Meta campaign, GHL contact).</summary>
    public bool IsUnmapped => !HasStripeSubscription || !HasMetaCampaign || !HasGhlContact;
}

/// <summary>Everything the client detail page shows, loaded in one factory scope.</summary>
public sealed record ClientDetail(
    Client Client,
    IReadOnlyList<IdentityLink> Links,
    IReadOnlyList<TrialPeriod> Trials,
    IReadOnlyList<LedgerEntry> Ledger);

public class ClientDirectoryService(IDbContextFactory<RdDbContext> factory)
{
    public async Task<List<ClientListRow>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Clients
            .OrderBy(c => c.BusinessName)
            .Select(c => new ClientListRow(
                c.Id,
                c.BusinessName,
                c.ContactName,
                c.ContractType,
                c.AccountType,
                c.EnforcementMode,
                c.Country,
                c.CurrencyCode,
                c.IdentityLinks.Any(l => l.System == ExternalSystem.Stripe && l.Kind == LinkKind.Customer && l.InvalidatedAt == null),
                c.IdentityLinks.Any(l => l.System == ExternalSystem.Stripe && l.Kind == LinkKind.Subscription && l.InvalidatedAt == null),
                c.IdentityLinks.Any(l => l.System == ExternalSystem.Meta && l.Kind == LinkKind.Campaign && l.InvalidatedAt == null),
                c.IdentityLinks.Any(l => l.System == ExternalSystem.Ghl && l.Kind == LinkKind.Contact && l.InvalidatedAt == null)))
            .ToListAsync(ct);
    }

    public async Task<ClientDetail?> GetDetailAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var client = await db.Clients.AsNoTracking()
            .Include(c => c.Package)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (client is null) return null;

        var links = await db.IdentityLinks.AsNoTracking()
            .Where(l => l.ClientId == id)
            .OrderBy(l => l.System).ThenBy(l => l.Kind).ThenByDescending(l => l.CreatedAt)
            .ToListAsync(ct);

        var trials = await db.TrialPeriods.AsNoTracking()
            .Where(t => t.ClientId == id)
            .OrderByDescending(t => t.StartsAt)
            .ToListAsync(ct);

        var ledger = await db.LedgerEntries.AsNoTracking()
            .Where(l => l.ClientId == id)
            .OrderByDescending(l => l.OccurredAt)
            .Take(200)
            .ToListAsync(ct);

        return new ClientDetail(client, links, trials, ledger);
    }
}
