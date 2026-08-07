using Microsoft.EntityFrameworkCore;
using RD.Domain;
using RD.Infrastructure;

namespace RD.Web.Services;

public sealed record InvestigationRow(
    Guid Id,
    InvestigationKind Kind,
    string Detail,
    InvestigationStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ResolvedAt,
    string? ResolvedBy,
    string? ResolutionNote,
    Guid? ClientId,
    string? ClientName,
    ExternalSystem? System,
    string? ExternalId);

/// <summary>
/// Generic reconciliation queue reads and resolve/dismiss writes. Structured
/// ownership and mapping fixes use MappingWizardService instead.
/// </summary>
public class ReconciliationService(IDbContextFactory<RdDbContext> factory, IClock clock)
{
    public async Task<List<InvestigationRow>> GetAsync(InvestigationKind? kind, bool openOnly, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var query = db.InvestigationItems.AsNoTracking()
            .Where(i => openOnly ? i.Status == InvestigationStatus.Open : i.Status != InvestigationStatus.Open);
        if (kind is not null)
            query = query.Where(i => i.Kind == kind);

        return await (from i in query
                      join c in db.Clients on i.ClientId equals c.Id into cj
                      from c in cj.DefaultIfEmpty()
                      orderby i.CreatedAt descending
                      select new InvestigationRow(
                          i.Id, i.Kind, i.Detail, i.Status, i.CreatedAt,
                          i.ResolvedAt, i.ResolvedBy, i.ResolutionNote,
                          i.ClientId, c != null ? c.BusinessName : null,
                          i.System, i.ExternalId))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Marks a generic item Resolved (or Dismissed) with a note. Stripe customer
    /// ownership items must use MappingWizardService so they cannot be closed
    /// without confirming the complete active customer set.
    /// </summary>
    public async Task<bool> ResolveAsync(Guid id, string note, bool dismiss, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var item = await db.InvestigationItems.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (item is null || item.Status != InvestigationStatus.Open)
            return false;
        if (item.Kind == InvestigationKind.DuplicateStripeCustomer)
            return false;

        item.Status = dismiss ? InvestigationStatus.Dismissed : InvestigationStatus.Resolved;
        item.ResolvedAt = clock.UtcNow;
        item.ResolvedBy = "operator"; // Identity integration (real user names) arrives with the auth wiring.
        item.ResolutionNote = note;
        await db.SaveChangesAsync(ct);
        return true;
    }
}
