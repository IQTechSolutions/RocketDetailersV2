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

/// <summary>Severity of an account-health issue, mapped to a MudBlazor alert severity in the UI.</summary>
public enum AccountIssueSeverity { Error = 0, Warning = 1, Info = 2 }

/// <summary>One thing wrong with a client's account, in plain English, with an optional deep-link to fix it.</summary>
public sealed record AccountIssue(
    AccountIssueSeverity Severity,
    string Title,
    string Detail,
    string? FixHref = null,
    string? FixLabel = null);

/// <summary>
/// Everything currently blocking a client from clean, enforceable operation:
/// missing/unverified required links (master accounts) and open reconciliation
/// items. Empty <see cref="Issues"/> means the account is healthy.
/// </summary>
public sealed record AccountHealth(IReadOnlyList<AccountIssue> Issues)
{
    public bool IsHealthy => Issues.Count == 0;
}

/// <summary>A duplicate account retired into this client. Surfaced on the survivor so a
/// merge can be reversed from where operators actually land — retired accounts drop
/// out of the directory, so their own page is otherwise unreachable.</summary>
public sealed record MergedAccountRow(Guid Id, string BusinessName, string? ContactName, DateTimeOffset? MergedAt);

/// <summary>Everything the client detail page shows, loaded in one factory scope.</summary>
public sealed record ClientDetail(
    Client Client,
    IReadOnlyList<IdentityLink> Links,
    IReadOnlyList<TrialPeriod> Trials,
    IReadOnlyList<LedgerEntry> Ledger,
    AccountHealth Health,
    IReadOnlyDictionary<Guid, string> ExternalLinks,
    IReadOnlyList<MergedAccountRow> Absorbed);

public class ClientDirectoryService(IDbContextFactory<RdDbContext> factory, VendorLinks vendorLinks)
{
    public async Task<List<ClientListRow>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Clients
            .Where(c => c.MergedIntoClientId == null) // retired duplicates drop out of the directory
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

        var openInvestigations = await db.InvestigationItems.AsNoTracking()
            .Where(i => i.ClientId == id && i.Status == InvestigationStatus.Open)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync(ct);

        var currentVerificationJson = await db.MappingVerifications.AsNoTracking()
            .Where(v => v.ClientId == id && v.InvalidatedAt == null)
            .OrderByDescending(v => v.VerifiedAt)
            .Select(v => v.VerifiedLinksJson)
            .FirstOrDefaultAsync(ct);
        var hasCurrentVerification = MappingVerificationCoverage.PinsAll(
            currentVerificationJson,
            links.Where(l => l.InvalidatedAt == null && RequiredLinks.IsRequired(l.System, l.Kind))
                .Select(l => (l.Id, l.LinkVersion)));

        var health = BuildHealth(client, links, openInvestigations, hasCurrentVerification);

        // Duplicates retired into this client — the survivor is where operators land,
        // so this is where an accidental merge is reversed from.
        var absorbed = await db.Clients.AsNoTracking()
            .Where(c => c.MergedIntoClientId == id)
            .OrderByDescending(c => c.MergedAt)
            .Select(c => new MergedAccountRow(c.Id, c.BusinessName, c.ContactName, c.MergedAt))
            .ToListAsync(ct);

        // GHL contact deep-links are location-scoped; resolve each contact's
        // location from the messages we've observed for it.
        var ghlContactIds = links
            .Where(l => l.System == ExternalSystem.Ghl && l.Kind == LinkKind.Contact)
            .Select(l => l.ExternalId)
            .ToList();
        var ghlLocationByContact = ghlContactIds.Count == 0
            ? new Dictionary<string, string>()
            : (await db.GhlMessages.AsNoTracking()
                    .Where(m => ghlContactIds.Contains(m.ContactId))
                    .Select(m => new { m.ContactId, m.LocationId, m.SentAt })
                    .ToListAsync(ct))
                .GroupBy(m => m.ContactId)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.SentAt).First().LocationId);

        var externalLinks = new Dictionary<Guid, string>();
        foreach (var l in links)
        {
            var location = l.System == ExternalSystem.Ghl && l.Kind == LinkKind.Contact
                ? ghlLocationByContact.GetValueOrDefault(l.ExternalId)
                : null;
            var url = vendorLinks.For(l.System, l.Kind, l.ExternalId, location);
            if (url is not null) externalLinks[l.Id] = url;
        }

        return new ClientDetail(client, links, trials, ledger, health, externalLinks, absorbed);
    }

    /// <summary>
    /// Derive the "what's wrong with this account" list. Structural link/verification
    /// gaps apply only to master-account clients (Own accounts pay Meta directly —
    /// no campaign enforcement, no exposure); open investigation items apply to all.
    /// Ordered most-severe first.
    /// </summary>
    private static AccountHealth BuildHealth(
        Client client,
        IReadOnlyList<IdentityLink> links,
        IReadOnlyList<InvestigationItem> openInvestigations,
        bool hasCurrentVerification)
    {
        var issues = new List<AccountIssue>();
        var active = links.Where(l => l.InvalidatedAt == null).ToList();
        var mappingHref = $"/mapping?clientId={client.Id}";

        if (client.AccountType == AccountType.Master)
        {
            var missing = RequiredLinks.All
                .Where(spec => !active.Any(l => l.System == spec.System && l.Kind == spec.Kind))
                .ToList();

            foreach (var spec in missing)
                issues.Add(new AccountIssue(
                    AccountIssueSeverity.Error,
                    $"Missing {spec.Label}",
                    $"{spec.HelpText} The engine is blind here until it's linked.",
                    mappingHref, "Fix mapping"));

            // Links complete but never verified — the reason the client is held in Shadow.
            if (missing.Count == 0 && !hasCurrentVerification)
                issues.Add(new AccountIssue(
                    AccountIssueSeverity.Warning,
                    "Mapping not verified",
                    "All four required links are present, but the mapping hasn't been verified — this client stays in Shadow until an operator verifies it.",
                    mappingHref, "Verify mapping"));
        }

        // Open reconciliation items flagged by the import or the engine.
        foreach (var item in openInvestigations)
            issues.Add(new AccountIssue(
                SeverityFor(item.Kind),
                item.Kind.Title(),
                item.Detail,
                $"/reconciliation?kind={item.Kind}", "Review"));

        return new AccountHealth(issues.OrderBy(i => (int)i.Severity).ToList());
    }

    private static AccountIssueSeverity SeverityFor(InvestigationKind kind) => kind switch
    {
        InvestigationKind.UnmappedIdentity
            or InvestigationKind.ExposureCapExceeded
            or InvestigationKind.ImportConflict => AccountIssueSeverity.Error,
        InvestigationKind.DuplicateStripeCustomer => AccountIssueSeverity.Warning,
        InvestigationKind.StaleSync
            or InvestigationKind.NonUsdCurrency
            or InvestigationKind.Other => AccountIssueSeverity.Info,
        _ => AccountIssueSeverity.Warning,
    };
}
