using Microsoft.EntityFrameworkCore;
using RD.Domain;
using RD.Infrastructure;

namespace RD.Web.Services;

/// <summary>One row of the cockpit action queue: proposed action + client + link evidence + mode.</summary>
public sealed record ActionQueueRow(
    Guid Id,
    OutboxActionType ActionType,
    OutboxStatus Status,
    EnforcementMode Mode,
    Guid ClientId,
    string ClientName,
    string? Evidence,
    bool HasStripe,
    bool HasMeta,
    bool HasGhl,
    DateTimeOffset CreatedAt,
    bool IsShadowVerdict = false,
    string? TitleOverride = null);

/// <summary>
/// Read side of the outbox for the cockpit. Approve/Dismiss are deliberate
/// stubs — the outbox executor (leasing, CAS on ActionVersion, revalidation)
/// is Lane D / M2 scope and replaces these bodies.
/// </summary>
public class OutboxActionService(IDbContextFactory<RdDbContext> factory)
{
    public async Task<List<ActionQueueRow>> GetOpenQueueAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var staged = await LoadStagedAsync(db, ct);
        var shadow = await LoadShadowVerdictsAsync(db, staged.Select(r => r.ClientId).ToHashSet(), ct);
        return staged.Concat(shadow).ToList();
    }

    private static async Task<List<ActionQueueRow>> LoadStagedAsync(RdDbContext db, CancellationToken ct)
    {
        return await (from a in db.OutboxActions
                      where a.Status == OutboxStatus.Pending || a.Status == OutboxStatus.AwaitingApproval
                      join c in db.Clients on a.ClientId equals c.Id
                      join d in db.Decisions on a.DecisionId equals d.Id into dj
                      from d in dj.DefaultIfEmpty()
                      orderby a.CreatedAt descending
                      select new ActionQueueRow(
                          a.Id,
                          a.ActionType,
                          a.Status,
                          c.EnforcementMode,
                          c.Id,
                          c.BusinessName,
                          d != null ? d.Reason : null,
                          c.IdentityLinks.Any(l => l.System == ExternalSystem.Stripe && l.Kind == LinkKind.Subscription && l.InvalidatedAt == null),
                          c.IdentityLinks.Any(l => l.System == ExternalSystem.Meta && l.Kind == LinkKind.Campaign && l.InvalidatedAt == null),
                          c.IdentityLinks.Any(l => l.System == ExternalSystem.Ghl && l.Kind == LinkKind.Contact && l.InvalidatedAt == null),
                          a.CreatedAt))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Shadow phase: the latest would-X verdict per client IS the queue row
    /// ("Would pause — no action taken", per the approved wireframe). Clients
    /// already staged in the outbox are excluded — one row per client.
    /// </summary>
    private static async Task<List<ActionQueueRow>> LoadShadowVerdictsAsync(
        RdDbContext db, HashSet<Guid> stagedClientIds, CancellationToken ct)
    {
        // Materialize first — composing Where AFTER GroupBy+First() trips an
        // EF translation bug (EmptyProjectionMember).
        var latest = (await db.Decisions
            .GroupBy(d => d.ClientId)
            .Select(g => g.OrderByDescending(d => d.EvaluatedAt).First())
            .ToListAsync(ct))
            .Where(d => d.ProposedAction
                is ProposedActionType.Pause or ProposedActionType.Resume
                or ProposedActionType.DunningStep or ProposedActionType.Escalate)
            .ToList();

        var clientIds = latest.Select(d => d.ClientId).Where(id => !stagedClientIds.Contains(id)).ToList();
        var clients = await db.Clients.AsNoTracking()
            .Where(c => clientIds.Contains(c.Id))
            .Select(c => new
            {
                c.Id, c.BusinessName, c.EnforcementMode,
                HasStripe = c.IdentityLinks.Any(l => l.System == ExternalSystem.Stripe && l.Kind == LinkKind.Subscription && l.InvalidatedAt == null),
                HasMeta = c.IdentityLinks.Any(l => l.System == ExternalSystem.Meta && l.Kind == LinkKind.Campaign && l.InvalidatedAt == null),
                HasGhl = c.IdentityLinks.Any(l => l.System == ExternalSystem.Ghl && l.Kind == LinkKind.Contact && l.InvalidatedAt == null),
            })
            .ToDictionaryAsync(c => c.Id, ct);

        return latest
            .Where(d => clients.ContainsKey(d.ClientId))
            .OrderBy(d => d.ProposedAction) // Pause first — the money rows lead
            .ThenByDescending(d => d.EvaluatedAt)
            .Select(d =>
            {
                var c = clients[d.ClientId];
                var (display, title) = d.ProposedAction switch
                {
                    ProposedActionType.Pause => (OutboxActionType.PauseCampaign, "Would pause ads — no action taken"),
                    ProposedActionType.Resume => (OutboxActionType.ResumeCampaign, "Would resume ads — no action taken"),
                    ProposedActionType.DunningStep => (OutboxActionType.TriggerGhlWorkflow, "Would send dunning message — no action taken"),
                    _ => (OutboxActionType.SendSlackAlert, "Needs a human decision"),
                };
                return new ActionQueueRow(
                    d.Id, display, OutboxStatus.Pending, c.EnforcementMode, d.ClientId, c.BusinessName,
                    d.Reason, c.HasStripe, c.HasMeta, c.HasGhl, d.EvaluatedAt,
                    IsShadowVerdict: true, TitleOverride: title);
            })
            .ToList();
    }

    /// <summary>Lane D: CAS on ActionVersion, status → Approved, ApprovalChannel = Cockpit, then the dispatcher leases it.</summary>
    public Task ApproveAsync(Guid actionId)
        => throw new NotImplementedException("Approve executes through the outbox dispatcher — that's Lane D (M2). The cockpit only stages the intent for now.");

    /// <summary>Lane D: CAS on ActionVersion, status → Dismissed with DismissReason.</summary>
    public Task DismissAsync(Guid actionId, string? reason)
        => throw new NotImplementedException("Dismiss executes through the outbox dispatcher — that's Lane D (M2). The cockpit only stages the intent for now.");
}
