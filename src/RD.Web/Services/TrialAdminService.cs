using Microsoft.EntityFrameworkCore;
using RD.Domain;
using RD.Infrastructure;

namespace RD.Web.Services;

/// <summary>Result of a trial write — success flag plus a human sentence for the snackbar.</summary>
public sealed record TrialWriteResult(bool Ok, string Message);

/// <summary>
/// Operator writes for trial periods. The one action today is backfilling a
/// missing expiry date — a trial with no end date is a needs-investigation gap,
/// not a free pass. Setting the date also closes any open MissingTrialExpiry
/// investigation for the client, so the fix and its audit trail land together.
/// </summary>
public class TrialAdminService(IDbContextFactory<RdDbContext> factory, IClock clock)
{
    private const string DefaultActor = "operator";

    public async Task<TrialWriteResult> SetExpiryAsync(Guid trialId, DateTimeOffset expiresAt, string actor = DefaultActor, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = clock.UtcNow;

        var trial = await db.TrialPeriods.FirstOrDefaultAsync(t => t.Id == trialId, ct);
        if (trial is null)
        {
            await tx.RollbackAsync(ct);
            return new TrialWriteResult(false, "Trial not found (maybe it changed in another tab).");
        }
        if (expiresAt < trial.StartsAt)
        {
            await tx.RollbackAsync(ct);
            return new TrialWriteResult(false, $"Expiry can't be before the trial started ({trial.StartsAt:yyyy-MM-dd}).");
        }

        trial.ExpiresAt = expiresAt;

        // The gap is now filled — close any open missing-expiry investigation for this client.
        var open = await db.InvestigationItems
            .Where(i => i.ClientId == trial.ClientId
                        && i.Kind == InvestigationKind.MissingTrialExpiry
                        && i.Status == InvestigationStatus.Open)
            .ToListAsync(ct);
        foreach (var i in open)
        {
            i.Status = InvestigationStatus.Resolved;
            i.ResolvedAt = now;
            i.ResolvedBy = string.IsNullOrWhiteSpace(actor) ? DefaultActor : actor;
            i.ResolutionNote = $"Trial expiry backfilled to {expiresAt:yyyy-MM-dd}.";
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return new TrialWriteResult(true, open.Count > 0
            ? $"Trial expiry set to {expiresAt:yyyy-MM-dd}; closed the missing-expiry investigation."
            : $"Trial expiry set to {expiresAt:yyyy-MM-dd}.");
    }
}
