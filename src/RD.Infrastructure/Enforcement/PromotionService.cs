using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RD.Domain;
using RD.Domain.Entities;

namespace RD.Infrastructure.Enforcement;

public sealed record LadderRow(Guid ClientId, string ClientName, PromotionLadder.Assessment Assessment);

public enum PromotionResult { Promoted, Blocked, NotFound }

/// <summary>
/// Drives the top of the enforcement ladder: assess Assist clients against the
/// Auto gates and perform the (re-checked) Assist→Auto promotion. Demotion
/// Auto→Assist is a manual step-down; drift-driven demotion to Shadow is the
/// policy's job, not this service's.
/// </summary>
public sealed class PromotionService(IDbContextFactory<RdDbContext> dbFactory, IClock clock, IOptions<EnforcementOptions> enforcement)
{
    private readonly int _threshold = enforcement.Value.CleanDaysForAuto;

    public async Task<PromotionLadder.Assessment?> AssessAsync(Guid clientId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var client = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId, ct);
        if (client is null) return null;
        var killEngaged = await KillEngagedAsync(db, ct);
        return await AssessInternalAsync(db, client, killEngaged, ct);
    }

    public async Task<IReadOnlyList<LadderRow>> AssessAllAssistAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var clients = await db.Clients.AsNoTracking()
            .Where(c => c.EnforcementMode == EnforcementMode.Assist)
            .OrderBy(c => c.BusinessName)
            .ToListAsync(ct);
        var killEngaged = await KillEngagedAsync(db, ct);

        var rows = new List<LadderRow>();
        foreach (var c in clients)
            rows.Add(new LadderRow(c.Id, c.BusinessName, await AssessInternalAsync(db, c, killEngaged, ct)));
        return rows;
    }

    /// <summary>Fleet distribution across the ladder rungs (master-account clients).</summary>
    public async Task<(int Shadow, int Assist, int Auto)> LadderCountsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var counts = await db.Clients.AsNoTracking()
            .Where(c => c.AccountType == AccountType.Master)
            .GroupBy(c => c.EnforcementMode)
            .Select(g => new { Mode = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        int For(EnforcementMode m) => counts.FirstOrDefault(x => x.Mode == m)?.Count ?? 0;
        return (For(EnforcementMode.Shadow), For(EnforcementMode.Assist), For(EnforcementMode.Auto));
    }

    /// <summary>Auto clients (for the ladder's "at the top" section).</summary>
    public async Task<IReadOnlyList<(Guid Id, string Name)>> AutoClientsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Clients.AsNoTracking()
            .Where(c => c.EnforcementMode == EnforcementMode.Auto)
            .OrderBy(c => c.BusinessName)
            .Select(c => new ValueTuple<Guid, string>(c.Id, c.BusinessName))
            .ToListAsync(ct);
    }

    /// <summary>Promote Assist→Auto only if the gate STILL passes at execution time (re-assessed, not trusted from the UI).</summary>
    public async Task<(PromotionResult Result, PromotionLadder.Assessment? Assessment)> PromoteToAutoAsync(Guid clientId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var client = await db.Clients.FirstOrDefaultAsync(c => c.Id == clientId, ct);
        if (client is null) return (PromotionResult.NotFound, null);

        var killEngaged = await KillEngagedAsync(db, ct);
        var assessment = await AssessInternalAsync(db, client, killEngaged, ct);
        if (!assessment.CanPromote) return (PromotionResult.Blocked, assessment);

        client.EnforcementMode = EnforcementMode.Auto;
        await db.SaveChangesAsync(ct);
        return (PromotionResult.Promoted, assessment);
    }

    /// <summary>Manual step-down Auto→Assist (always allowed — caution is never blocked).</summary>
    public async Task<PromotionResult> DemoteToAssistAsync(Guid clientId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var client = await db.Clients.FirstOrDefaultAsync(c => c.Id == clientId, ct);
        if (client is null) return PromotionResult.NotFound;
        if (client.EnforcementMode == EnforcementMode.Auto)
        {
            client.EnforcementMode = EnforcementMode.Assist;
            await db.SaveChangesAsync(ct);
        }
        return PromotionResult.Promoted;
    }

    private async Task<PromotionLadder.Assessment> AssessInternalAsync(RdDbContext db, Client client, bool killEngaged, CancellationToken ct)
    {
        var now = clock.UtcNow;

        var assistSince = await db.Decisions.AsNoTracking()
            .Where(d => d.ClientId == client.Id && d.Mode == EnforcementMode.Assist)
            .MinAsync(d => (DateTimeOffset?)d.EvaluatedAt, ct);

        // Unclean days: engine needed a human override or flagged investigation.
        var investigationDays = await db.Decisions.AsNoTracking()
            .Where(d => d.ClientId == client.Id
                        && (d.ProposedAction == ProposedActionType.Investigate || d.ProposedAction == ProposedActionType.Escalate))
            .Select(d => d.EvaluatedAt).ToListAsync(ct);
        var dismissedDays = await db.OutboxActions.AsNoTracking()
            .Where(a => a.ClientId == client.Id && a.Status == OutboxStatus.Dismissed)
            .Select(a => a.CreatedAt).ToListAsync(ct);
        var uncleanDays = investigationDays.Concat(dismissedDays)
            .Select(t => DateOnly.FromDateTime(t.UtcDateTime)).ToHashSet();

        var exercised = await db.OutboxActions.AsNoTracking()
            .AnyAsync(a => a.ClientId == client.Id && a.Status == OutboxStatus.Executed, ct);

        var mappingVerified = await db.MappingVerifications.AsNoTracking()
            .AnyAsync(v => v.ClientId == client.Id && v.InvalidatedAt == null, ct);

        return PromotionLadder.Assess(
            client.EnforcementMode, assistSince, uncleanDays, exercised, mappingVerified, killEngaged, now, _threshold);
    }

    private static async Task<bool> KillEngagedAsync(RdDbContext db, CancellationToken ct) =>
        await db.KillSwitch.AsNoTracking().Where(k => k.Id == 1).Select(k => k.Enabled).FirstOrDefaultAsync(ct);
}
