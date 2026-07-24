using Microsoft.EntityFrameworkCore;
using RD.Domain;
using RD.Domain.Entities;

namespace RD.Infrastructure.Enforcement;

/// <summary>
/// The global stop. Enabling bumps the epoch (so every in-flight outbox action
/// supersedes at its pre-write epoch check) AND reverts every client to Shadow.
/// It is not "instant" in the impossible sense — an action mid-write completes —
/// but nothing NEW executes, and the next dispatcher pass refuses everything.
/// </summary>
public sealed class KillSwitchService(IDbContextFactory<RdDbContext> dbFactory, IClock clock)
{
    public Task<long> EngageAsync(string actor, string reason, CancellationToken ct = default)
        => SetAsync(enabled: true, actor, reason, ct);

    public Task<long> ReleaseAsync(string actor, string reason, CancellationToken ct = default)
        => SetAsync(enabled: false, actor, reason, ct);

    public async Task<bool> IsEngagedAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.KillSwitch.AsNoTracking().Where(k => k.Id == 1).Select(k => k.Enabled).FirstOrDefaultAsync(ct);
    }

    private async Task<long> SetAsync(bool enabled, string actor, string reason, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = clock.UtcNow;

        var ks = await db.KillSwitch.FirstOrDefaultAsync(k => k.Id == 1, ct);
        if (ks is null)
        {
            ks = new KillSwitchState { Id = 1, Epoch = 0 };
            db.KillSwitch.Add(ks);
        }
        ks.Enabled = enabled;
        ks.Epoch++; // every bump invalidates actions staged under the old epoch
        ks.Actor = actor;
        ks.Reason = reason;
        ks.ChangedAt = now;
        await db.SaveChangesAsync(ct);

        if (enabled)
        {
            // Revert the whole fleet to Shadow — the ladder must be re-climbed
            // deliberately after the stop is released.
            await db.Clients.Where(c => c.EnforcementMode != EnforcementMode.Shadow)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.EnforcementMode, EnforcementMode.Shadow), ct);
        }
        return ks.Epoch;
    }
}
