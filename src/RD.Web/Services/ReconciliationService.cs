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
    string? ClientName);

/// <summary>
/// The reconciliation work queue. This is the ONLY service in the UI that
/// writes to the database (resolve/dismiss of InvestigationItems).
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
                          i.ClientId, c != null ? c.BusinessName : null))
            .ToListAsync(ct);
    }

    /// <summary>Marks an item Resolved (or Dismissed) with a note. Returns false if it was already handled by someone else.</summary>
    public async Task<bool> ResolveAsync(Guid id, string note, bool dismiss, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var item = await db.InvestigationItems.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (item is null || item.Status != InvestigationStatus.Open)
            return false;

        item.Status = dismiss ? InvestigationStatus.Dismissed : InvestigationStatus.Resolved;
        item.ResolvedAt = clock.UtcNow;
        item.ResolvedBy = "operator"; // Identity integration (real user names) arrives with the auth wiring.
        item.ResolutionNote = note;
        await db.SaveChangesAsync(ct);
        return true;
    }
}
