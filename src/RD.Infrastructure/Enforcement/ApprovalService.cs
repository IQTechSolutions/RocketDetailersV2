using Microsoft.EntityFrameworkCore;
using RD.Domain;
using RD.Domain.Entities;

namespace RD.Infrastructure.Enforcement;

public enum ApprovalOutcome { Approved, Dismissed, AlreadyResolved, NotFound }

/// <summary>
/// Approve/Dismiss with compare-and-swap so the two doors — cockpit and Slack —
/// can never both act. Only AwaitingApproval actions are actionable; the first
/// writer flips the status and bumps ActionVersion, and the loser (whether a
/// second click or the other channel) gets AlreadyResolved via the RowVersion
/// concurrency check. Approved actions are then picked up by the dispatcher,
/// which STILL revalidates before writing — approval is intent, not execution.
/// </summary>
public sealed class ApprovalService(IDbContextFactory<RdDbContext> dbFactory, IClock clock)
{
    public Task<ApprovalOutcome> ApproveAsync(Guid actionId, ApprovalChannel channel, string approver, CancellationToken ct = default)
        => TransitionAsync(actionId, channel, approver, dismiss: false, reason: null, ct);

    public Task<ApprovalOutcome> DismissAsync(Guid actionId, ApprovalChannel channel, string actor, string? reason, CancellationToken ct = default)
        => TransitionAsync(actionId, channel, actor, dismiss: true, reason, ct);

    private async Task<ApprovalOutcome> TransitionAsync(
        Guid actionId, ApprovalChannel channel, string actor, bool dismiss, string? reason, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var action = await db.OutboxActions.FirstOrDefaultAsync(a => a.Id == actionId, ct);
        if (action is null) return ApprovalOutcome.NotFound;
        if (action.Status != OutboxStatus.AwaitingApproval) return ApprovalOutcome.AlreadyResolved;

        if (dismiss)
        {
            action.Status = OutboxStatus.Dismissed;
            action.DismissReason = reason;
        }
        else
        {
            action.Status = OutboxStatus.Approved;
        }
        action.ApprovedBy = actor;
        action.ApprovalChannel = channel;
        action.ActionVersion++;

        try
        {
            await db.SaveChangesAsync(ct);
            return dismiss ? ApprovalOutcome.Dismissed : ApprovalOutcome.Approved;
        }
        catch (DbUpdateConcurrencyException)
        {
            // The other channel (or a double-click) won the race — RowVersion
            // moved under us. That's the CAS doing its job, not an error.
            return ApprovalOutcome.AlreadyResolved;
        }
    }
}
