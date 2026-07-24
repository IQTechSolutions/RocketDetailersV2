using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RD.Domain;
using RD.Domain.Entities;

namespace RD.Infrastructure.Slack;

/// <summary>
/// Recurring job: posts every AwaitingApproval action to Slack exactly once.
/// Selects actions with <c>SlackNotifiedAt == null</c>, resolves the client name
/// and the decision reason, posts via <see cref="SlackNotifier"/>, then stamps
/// <c>SlackNotifiedAt</c> — so a later run never re-posts. A post that throws
/// leaves the marker null (retried next cycle) and does not block the batch.
///
/// If Slack:IncomingWebhookUrl is unconfigured the job is a no-op (logged once
/// per run at Information) — an unconfigured integration is silence, not noise.
/// </summary>
public sealed class SlackNotificationJob(
    IDbContextFactory<RdDbContext> dbFactory,
    SlackNotifier notifier,
    IClock clock,
    ILogger<SlackNotificationJob> logger)
{
    public async Task RunAsync(CancellationToken ct)
    {
        if (!notifier.IsConfigured)
        {
            // The host is expected to gate scheduling on Slack:IncomingWebhookUrl; this
            // is a defensive no-op, logged at Debug so it can never spam the log.
            logger.LogDebug("Slack:IncomingWebhookUrl not configured — SlackNotificationJob is a no-op.");
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = clock.UtcNow;

        var pending = await db.OutboxActions
            .Where(a => a.Status == OutboxStatus.AwaitingApproval && a.SlackNotifiedAt == null)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(ct);
        if (pending.Count == 0) return;

        var posted = 0;
        foreach (var action in pending)
        {
            var clientName = await db.Clients
                .Where(c => c.Id == action.ClientId)
                .Select(c => c.BusinessName)
                .FirstOrDefaultAsync(ct) ?? "(unknown client)";

            var reason = await ResolveReasonAsync(db, action, ct);

            try
            {
                await notifier.PostActionAsync(action, clientName, reason, ct);
                action.SlackNotifiedAt = now;
                await db.SaveChangesAsync(ct);
                posted++;
            }
            catch (Exception ex)
            {
                // Leave SlackNotifiedAt null so the next run retries this one action.
                logger.LogError(ex, "Failed to post OutboxAction {ActionId} to Slack; will retry next run.", action.Id);
            }
        }

        if (posted > 0)
            logger.LogInformation("Slack notifier posted {Posted} awaiting-approval action(s).", posted);
    }

    /// <summary>Prefer the reason from the decision that produced this action; fall back to the client's latest decision.</summary>
    private static async Task<string> ResolveReasonAsync(RdDbContext db, OutboxAction action, CancellationToken ct)
    {
        var reason = await db.Decisions
            .Where(d => d.Id == action.DecisionId)
            .Select(d => d.Reason)
            .FirstOrDefaultAsync(ct);

        reason ??= await db.Decisions
            .Where(d => d.ClientId == action.ClientId)
            .OrderByDescending(d => d.EvaluatedAt)
            .Select(d => d.Reason)
            .FirstOrDefaultAsync(ct);

        return reason ?? "";
    }
}
