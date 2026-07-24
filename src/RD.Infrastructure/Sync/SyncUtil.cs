using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RD.Domain;
using RD.Domain.Entities;

namespace RD.Infrastructure.Sync;

internal static class SyncUtil
{
    public const int ErrorMaxLength = 2000;

    public static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    /// <summary>
    /// Records a failed sweep on its SyncRun row. The change tracker is cleared
    /// first so partial sweep state never rides along with the failure record —
    /// a failed run must stay red AND must not half-commit its page.
    /// Never throws (best effort; the scheduler retries next cycle anyway).
    /// </summary>
    public static async Task RecordFailureAsync(RdDbContext db, SyncRun run, Exception failure, ILogger logger)
    {
        try
        {
            db.ChangeTracker.Clear();
            run.Status = SyncRunStatus.Failed;
            run.CompletedAt = null; // a failed sweep is never green
            run.Error = Truncate(failure.ToString(), ErrorMaxLength);
            db.SyncRuns.Update(run);
            // CancellationToken.None: even a cancelled job records its failure.
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception recordError)
        {
            logger.LogError(recordError, "Could not record failure for SyncRun {SyncRunId}", run.Id);
        }
    }
}

internal static class LedgerIngest
{
    /// <summary>
    /// Inserts ledger candidates idempotently against the unique
    /// (SourceSystem, SourceObjectId, Type) ingestion key. Check-before-insert
    /// covers re-runs of the sweep; the duplicate-key catch covers the race
    /// with concurrent ingestion (webhooks land in M2), degrading to
    /// row-by-row insert-or-skip. Money is never double-counted.
    /// </summary>
    public static async Task<int> InsertIdempotentAsync(
        RdDbContext db, IReadOnlyList<LedgerEntry> candidates, CancellationToken ct)
    {
        if (candidates.Count == 0) return 0;

        // De-dup within the batch itself (a vendor page glitch must not fault the sweep).
        var distinct = candidates
            .GroupBy(c => (c.SourceSystem, c.SourceObjectId, c.Type))
            .Select(g => g.First())
            .ToList();

        var sourceIds = distinct.Select(c => c.SourceObjectId).Distinct().ToList();
        var existing = await db.LedgerEntries
            .Where(l => sourceIds.Contains(l.SourceObjectId))
            .Select(l => new { l.SourceSystem, l.SourceObjectId, l.Type })
            .ToListAsync(ct);
        var existingKeys = existing
            .Select(e => (e.SourceSystem, e.SourceObjectId, e.Type))
            .ToHashSet();

        var toInsert = distinct
            .Where(c => !existingKeys.Contains((c.SourceSystem, c.SourceObjectId, c.Type)))
            .ToList();
        if (toInsert.Count == 0) return 0;

        db.LedgerEntries.AddRange(toInsert);
        try
        {
            await db.SaveChangesAsync(ct);
            return toInsert.Count;
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            // Race: someone inserted between our check and our save. Fall back
            // to one-by-one insert-or-skip so the non-duplicates still land.
            foreach (var entry in toInsert)
                db.Entry(entry).State = EntityState.Detached;

            var inserted = 0;
            foreach (var entry in toInsert)
            {
                db.LedgerEntries.Add(entry);
                try
                {
                    await db.SaveChangesAsync(ct);
                    inserted++;
                }
                catch (DbUpdateException retryEx) when (IsDuplicateKey(retryEx))
                {
                    db.Entry(entry).State = EntityState.Detached;
                }
            }
            return inserted;
        }
    }

    private static bool IsDuplicateKey(DbUpdateException ex)
    {
        for (Exception? inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 })
                return true;
        }
        return false;
    }
}
