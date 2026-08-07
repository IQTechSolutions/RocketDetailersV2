using Microsoft.EntityFrameworkCore;
using RD.Domain;

namespace RD.Infrastructure.Reconciliation;

/// <summary>
/// Closes the investigation rows emitted by the legacy Stripe discovery import
/// that incorrectly described invoice delinquency as an external Meta pause.
/// The original row remains intact; only its resolution audit fields change.
/// </summary>
public sealed class LegacyInvestigationCleanup(
    IDbContextFactory<RdDbContext> factory,
    IClock clock)
{
    public const string LegacyDetail =
        "At least one Stripe customer here is delinquent (unpaid invoice).";

    public const string SystemActor = "system:legacy-stripe-delinquency-cleanup";

    public const string AuditNote =
        "Legacy Stripe discovery import incorrectly classified delinquency as an external Meta pause. " +
        "Dismissed automatically; original classification retained for audit.";

    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var resolvedAt = clock.UtcNow;

        // SQL Server's normal text equality is case-insensitive and ignores
        // trailing spaces. Select the already-narrow candidate set first, then
        // apply the legacy fingerprint with ordinal .NET semantics so a similar
        // operator-authored detail can never be swept into this cleanup.
        var candidates = await db.InvestigationItems.AsNoTracking()
            .Where(i => i.Status == InvestigationStatus.Open
                        && i.Kind == InvestigationKind.ExternallyPausedPayment
                        && i.ClientId != null
                        && i.System == null
                        && i.ExternalId == null
                        && i.ResolvedAt == null
                        && i.ResolvedBy == null
                        && i.ResolutionNote == null)
            .Select(i => new { i.Id, i.Detail })
            .ToListAsync(ct);

        var exactIds = candidates
            .Where(i => string.Equals(i.Detail, LegacyDetail, StringComparison.Ordinal))
            .Select(i => i.Id)
            .ToArray();

        var affected = 0;
        foreach (var idBatch in exactIds.Chunk(500))
        {
            affected += await db.InvestigationItems
                .Where(i => idBatch.Contains(i.Id)
                            && i.Status == InvestigationStatus.Open
                            && i.ResolvedAt == null
                            && i.ResolvedBy == null
                            && i.ResolutionNote == null)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(i => i.Status, InvestigationStatus.Dismissed)
                    .SetProperty(i => i.ResolvedAt, resolvedAt)
                    .SetProperty(i => i.ResolvedBy, SystemActor)
                    .SetProperty(i => i.ResolutionNote, AuditNote), ct);
        }

        return affected;
    }
}
