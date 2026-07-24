using Hangfire;
using Microsoft.EntityFrameworkCore;
using RD.Domain;
using RD.Infrastructure;
using RD.Infrastructure.Sync;
using RD.Infrastructure.Webhooks;

namespace RD.Web.Services;

/// <summary>A permanently-failed external write awaiting an operator decision.</summary>
public sealed record DeadLetterRow(
    Guid Id, OutboxActionType ActionType, Guid ClientId, string ClientName,
    int Attempts, string? LastError, DateTimeOffset CreatedAt);

/// <summary>A webhook event that threw during processing — recorded, never lost, replayable.</summary>
public sealed record PoisonedWebhookRow(
    Guid Id, ExternalSystem System, string ExternalEventId, string EventType,
    int Attempts, string? LastError, DateTimeOffset ReceivedAt);

/// <summary>One sync sweep with its status/error — the freshness story behind StaleSync items.</summary>
public sealed record SyncRunRow(
    Guid Id, ExternalSystem System, SyncRunStatus Status,
    DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, int ItemsSeen, string? Error);

/// <summary>Result of an ops write — success flag plus a human sentence for the snackbar.</summary>
public sealed record OpsWriteResult(bool Ok, string Message);

/// <summary>
/// The operator surface for failure states that otherwise stay invisible:
/// dead-lettered outbox actions, poisoned webhook events, and failed sync runs.
/// Reads are cheap projections; the writes are deliberately conservative —
/// retry re-queues an action for the dispatcher to REVALIDATE (never a direct
/// external write from here), replay re-runs idempotent webhook side effects,
/// and a manual sync just enqueues the existing Hangfire job.
/// </summary>
public class OpsService(
    IDbContextFactory<RdDbContext> factory,
    IClock clock,
    StripeWebhookIngestor ingestor,
    IBackgroundJobClient jobs)
{
    // ---------------- Dead-lettered outbox actions ----------------

    public async Task<List<DeadLetterRow>> GetDeadLetteredAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await (from a in db.OutboxActions.AsNoTracking()
                      where a.Status == OutboxStatus.DeadLettered
                      join c in db.Clients on a.ClientId equals c.Id into cj
                      from c in cj.DefaultIfEmpty()
                      orderby a.CreatedAt descending
                      select new DeadLetterRow(
                          a.Id, a.ActionType, a.ClientId,
                          c != null ? c.BusinessName : "(unknown client)",
                          a.Attempts, a.LastError, a.CreatedAt))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Re-queue a dead-lettered action. It returns to Approved with a fresh attempt
    /// budget and the current kill-switch epoch; the dispatcher then re-evaluates the
    /// live policy before it writes, so a stale action that no longer applies is
    /// superseded rather than executed.
    /// </summary>
    public async Task<OpsWriteResult> RetryActionAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var action = await db.OutboxActions.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (action is null) return new OpsWriteResult(false, "Action not found (maybe it changed in another tab).");
        if (action.Status != OutboxStatus.DeadLettered) return new OpsWriteResult(false, "Only dead-lettered actions can be retried.");

        var epoch = await db.KillSwitch.AsNoTracking().Where(k => k.Id == 1).Select(k => (long?)k.Epoch).FirstOrDefaultAsync(ct) ?? 0;

        action.Status = OutboxStatus.Approved;
        action.Attempts = 0;
        action.NextAttemptAt = clock.UtcNow;
        action.LeaseUntil = null;
        action.LeaseOwner = null;
        action.FencingToken = null;
        action.LastError = null;
        action.ExpectedKillSwitchEpoch = epoch;
        await db.SaveChangesAsync(ct);
        return new OpsWriteResult(true, "Re-queued — the dispatcher revalidates against live policy before it acts.");
    }

    /// <summary>Close a dead-lettered action without retrying, with a note for the audit trail.</summary>
    public async Task<OpsWriteResult> DismissActionAsync(Guid id, string reason, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var action = await db.OutboxActions.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (action is null) return new OpsWriteResult(false, "Action not found.");
        if (action.Status != OutboxStatus.DeadLettered) return new OpsWriteResult(false, "Only dead-lettered actions can be dismissed here.");

        action.Status = OutboxStatus.Dismissed;
        action.DismissReason = string.IsNullOrWhiteSpace(reason) ? "Dismissed from Operations." : reason.Trim();
        await db.SaveChangesAsync(ct);
        return new OpsWriteResult(true, "Dismissed.");
    }

    // ---------------- Poisoned webhooks ----------------

    public async Task<List<PoisonedWebhookRow>> GetPoisonedWebhooksAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.WebhookInbox.AsNoTracking()
            .Where(w => w.Status == WebhookStatus.Poisoned)
            .OrderByDescending(w => w.ReceivedAt)
            .Select(w => new PoisonedWebhookRow(w.Id, w.System, w.ExternalEventId, w.EventType, w.Attempts, w.LastError, w.ReceivedAt))
            .ToListAsync(ct);
    }

    /// <summary>Re-run a poisoned webhook's side effects. Idempotent — money is never double-applied.</summary>
    public async Task<OpsWriteResult> ReplayWebhookAsync(Guid id, CancellationToken ct = default)
    {
        var result = await ingestor.ReplayAsync(id, ct);
        return result switch
        {
            WebhookIngestResult.Processed => new OpsWriteResult(true, "Replayed — the event processed cleanly."),
            WebhookIngestResult.AlreadyProcessed => new OpsWriteResult(true, "Already processed — nothing to replay."),
            _ => new OpsWriteResult(false, "Replay threw again — still poisoned. Read the error before retrying."),
        };
    }

    // ---------------- Sync runs ----------------

    public async Task<List<SyncRunRow>> GetRecentSyncRunsAsync(int take = 30, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.SyncRuns.AsNoTracking()
            .OrderByDescending(s => s.StartedAt)
            .Take(take)
            .Select(s => new SyncRunRow(s.Id, s.System, s.Status, s.StartedAt, s.CompletedAt, s.ItemsSeen, s.Error))
            .ToListAsync(ct);
    }

    /// <summary>Enqueue a one-off sync sweep for a vendor via the existing Hangfire job.</summary>
    public OpsWriteResult TriggerSync(ExternalSystem system)
    {
        switch (system)
        {
            case ExternalSystem.Stripe: jobs.Enqueue<StripeSyncJob>(j => j.RunAsync(CancellationToken.None)); break;
            case ExternalSystem.Meta: jobs.Enqueue<MetaSyncJob>(j => j.RunAsync(CancellationToken.None)); break;
            case ExternalSystem.Ghl: jobs.Enqueue<GhlMessageSyncJob>(j => j.RunAsync(CancellationToken.None)); break;
            default: return new OpsWriteResult(false, $"No sync job is wired for {system}.");
        }
        return new OpsWriteResult(true, $"{system} sync queued — it runs in the background; refresh in a moment.");
    }
}
