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
    DateTimeOffset CreatedAt);

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

    /// <summary>Lane D: CAS on ActionVersion, status → Approved, ApprovalChannel = Cockpit, then the dispatcher leases it.</summary>
    public Task ApproveAsync(Guid actionId)
        => throw new NotImplementedException("Approve executes through the outbox dispatcher — that's Lane D (M2). The cockpit only stages the intent for now.");

    /// <summary>Lane D: CAS on ActionVersion, status → Dismissed with DismissReason.</summary>
    public Task DismissAsync(Guid actionId, string? reason)
        => throw new NotImplementedException("Dismiss executes through the outbox dispatcher — that's Lane D (M2). The cockpit only stages the intent for now.");
}
