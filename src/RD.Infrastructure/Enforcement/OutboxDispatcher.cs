using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RD.Domain;
using RD.Domain.Entities;
using RD.Domain.Policy;
using RD.Infrastructure.Gateways;

namespace RD.Infrastructure.Enforcement;

/// <summary>
/// The single retry authority for external writes. Every Meta/GHL mutation
/// flows through here — never fire-and-forget. Per action:
///
///   claim (atomic lease) ─▶ kill-switch epoch check ─▶ REVALIDATE (re-run the
///   policy on fresh state) ─▶ execute with read-back convergence ─▶ Executed
///                    │                    │
///                    │                    └─▶ verdict changed ⇒ SUPERSEDED (never write)
///                    └─▶ epoch changed ⇒ SUPERSEDED
///
///   execute throws ─▶ retry (release + backoff) until MaxAttempts ─▶ DeadLettered
///
/// Revalidation is the F4 guard: an Approve that a human clicked at 09:06 will
/// NOT pause a client who paid at 09:04, because the verdict is recomputed
/// immediately before the write and no longer says "pause".
/// </summary>
public sealed class OutboxDispatcher(
    IDbContextFactory<RdDbContext> dbFactory,
    ClientStateBuilder stateBuilder,
    IMetaAdsGateway meta,
    IGhlGateway ghl,
    IClock clock,
    IOptions<EnforcementOptions> enforcement,
    ILogger<OutboxDispatcher> logger,
    Func<Guid, CancellationToken, Task>? beforeClientFence = null)
{
    private readonly EnforcementOptions _enf = enforcement.Value;

    public async Task RunAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = clock.UtcNow;

        var killSwitch = await db.KillSwitch.AsNoTracking().FirstOrDefaultAsync(k => k.Id == 1, ct);
        if (killSwitch is { Enabled: true })
        {
            logger.LogWarning("Outbox dispatcher: kill switch is ENABLED — no actions dispatched.");
            return;
        }

        var owner = $"{Environment.MachineName}:{Environment.ProcessId}";
        var claimedIds = await ClaimBatchAsync(db, owner, now, ct);
        if (claimedIds.Count == 0) return;

        var actions = await db.OutboxActions.Where(a => claimedIds.Contains(a.Id)).ToListAsync(ct);

        var executed = 0; var superseded = 0; var failed = 0; var deadLettered = 0;

        foreach (var action in actions.OrderBy(a => a.SequenceGroup).ThenBy(a => a.SequenceNo))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Serialize the final revalidation + vendor I/O with mapping and
                // subscription changes for this client. A mapper may have
                // superseded a claimed lease while waiting for this fence, so
                // reload and prove this dispatcher still owns it before acting.
                if (beforeClientFence is not null)
                    await beforeClientFence(action.ClientId, ct);
                await using var mutationFence = await ClientMutationFence.AcquireAsync(db, action.ClientId, ct);
                await db.Entry(action).ReloadAsync(ct);
                if (action.Status != OutboxStatus.Leased
                    || !string.Equals(action.LeaseOwner, owner, StringComparison.Ordinal))
                    continue;

                // Reload vendor freshness only after the client fence is held.
                // BuildAsync independently reads merge membership inside this
                // protected window before rolling up append-only ledger rows.
                var ctx = await stateBuilder.LoadContextAsync(db, ct);
                var outcome = await ProcessAsync(db, action, ctx, now, ct);
                switch (outcome)
                {
                    case Outcome.Executed: executed++; break;
                    case Outcome.Superseded: superseded++; break;
                }
            }
            catch (Exception ex)
            {
                // Safety refusals (blocked writes) are permanent — dead-letter immediately.
                var permanent = ex is MetaWriteBlockedException;
                if (permanent || action.Attempts >= _enf.MaxAttempts)
                {
                    action.Status = OutboxStatus.DeadLettered;
                    action.LastError = Truncate(ex.Message, 2000);
                    action.LeaseUntil = null;
                    deadLettered++;
                    logger.LogError(ex, "Outbox action {Id} ({Type}) dead-lettered after {Attempts} attempt(s).",
                        action.Id, action.ActionType, action.Attempts);
                }
                else
                {
                    action.Status = OutboxStatus.Approved; // release for another pass
                    action.LeaseUntil = null;
                    action.NextAttemptAt = now + Backoff(action.Attempts);
                    action.LastError = Truncate(ex.Message, 2000);
                    failed++;
                    logger.LogWarning(ex, "Outbox action {Id} ({Type}) failed (attempt {Attempts}) — will retry.",
                        action.Id, action.ActionType, action.Attempts);
                }
                await SaveTolerantAsync(db, ct);
            }
        }

        logger.LogInformation(
            "Outbox dispatch: {Claimed} claimed, {Executed} executed, {Superseded} superseded, {Failed} retrying, {Dead} dead-lettered.",
            claimedIds.Count, executed, superseded, failed, deadLettered);
    }

    private enum Outcome { Executed, Superseded }

    private async Task<Outcome> ProcessAsync(RdDbContext db, OutboxAction action, ClientStateBuilder.Context ctx, DateTimeOffset now, CancellationToken ct)
    {
        // Kill-switch epoch re-check immediately before any work — a toggle
        // between staging and now supersedes the action.
        var epoch = await db.KillSwitch.AsNoTracking().Where(k => k.Id == 1).Select(k => (long?)k.Epoch).FirstOrDefaultAsync(ct) ?? 0;
        if (epoch != action.ExpectedKillSwitchEpoch)
            return await SupersedeAsync(db, action, "Kill-switch epoch changed since staging.", ct);

        var client = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == action.ClientId, ct);
        if (client is null)
            return await SupersedeAsync(db, action, "Client no longer exists.", ct);
        if (client.EnforcementMode == EnforcementMode.Shadow)
            return await SupersedeAsync(
                db,
                action,
                "Client was demoted to Shadow after this action was staged; Shadow mode never performs vendor writes.",
                ct);

        var state = await stateBuilder.BuildAsync(db, client, ctx, ct);
        var verdict = EligibilityPolicy.Evaluate(state);

        if (!VerdictStillMatches(action, verdict))
            return await SupersedeAsync(db, action, $"World changed: policy now says '{verdict.Action}: {verdict.Reason}', not '{action.ActionType}'.", ct, racedVerdict: verdict, state: state);

        // Verdict holds — execute with read-back convergence.
        switch (action.ActionType)
        {
            case OutboxActionType.PauseCampaign: await ExecutePauseAsync(db, action, now, ct); break;
            case OutboxActionType.ResumeCampaign: await ExecuteResumeAsync(db, action, now, ct); break;
            case OutboxActionType.WriteGhlField: await ExecuteGhlFieldAsync(action, ct); break;
            case OutboxActionType.TriggerGhlWorkflow: await ExecuteGhlTriggerAsync(db, action, now, ct); break;
            default: throw new NotSupportedException($"Dispatcher cannot execute {action.ActionType}.");
        }

        action.Status = OutboxStatus.Executed;
        action.ExecutedAt = now;
        action.LeaseUntil = null;
        await SaveTolerantAsync(db, ct);
        return Outcome.Executed;
    }

    private static bool VerdictStillMatches(OutboxAction action, PolicyDecision verdict) => action.ActionType switch
    {
        OutboxActionType.PauseCampaign => verdict.Action == ProposedActionType.Pause,
        OutboxActionType.ResumeCampaign => verdict.Action == ProposedActionType.Resume,
        OutboxActionType.WriteGhlField or OutboxActionType.TriggerGhlWorkflow =>
            verdict.Action == ProposedActionType.DunningStep && StepOf(action) == verdict.DunningStep,
        _ => false,
    };

    private static int? StepOf(OutboxAction action) => action.ActionType switch
    {
        OutboxActionType.WriteGhlField => JsonSerializer.Deserialize<GhlFieldPayload>(action.PayloadJson)?.Step,
        OutboxActionType.TriggerGhlWorkflow => JsonSerializer.Deserialize<GhlWorkflowPayload>(action.PayloadJson)?.Step,
        _ => null,
    };

    private async Task ExecutePauseAsync(RdDbContext db, OutboxAction action, DateTimeOffset now, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<CampaignActionPayload>(action.PayloadJson)!;
        foreach (var campaignId in payload.CampaignIds)
        {
            // Capture prior status BEFORE mutating — restoration touches only what we paused.
            var before = await meta.GetCampaignAsync(campaignId, ct);
            if (before is null || before.EffectiveStatus is "PAUSED" or "CAMPAIGN_PAUSED")
                continue; // already paused (or gone) — idempotent no-op

            var result = await meta.PauseCampaignAsync(campaignId, ct);
            if (!result.Converged)
                throw new HttpRequestException($"Pause of {campaignId} did not converge (observed {result.ObservedEffectiveStatus}).");

            db.PauseOperations.Add(new PauseOperation
            {
                Id = Guid.NewGuid(), ClientId = action.ClientId, OutboxActionId = action.Id,
                EntityType = MetaEntityType.Campaign, ExternalId = campaignId,
                PriorStatus = before.EffectiveStatus, RemoteVersion = result.RemoteVersion,
                State = PauseOperationState.Paused, PausedAt = now,
            });
        }
    }

    private async Task ExecuteResumeAsync(RdDbContext db, OutboxAction action, DateTimeOffset now, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<CampaignActionPayload>(action.PayloadJson)!;
        foreach (var campaignId in payload.CampaignIds)
        {
            var op = await db.PauseOperations
                .Where(p => p.ClientId == action.ClientId && p.ExternalId == campaignId && p.State == PauseOperationState.Paused)
                .OrderByDescending(p => p.PausedAt)
                .FirstOrDefaultAsync(ct);
            if (op is null) continue; // we never paused this one — never resume someone else's pause

            var result = await meta.ResumeCampaignAsync(campaignId, ct);
            if (!result.Converged)
                throw new HttpRequestException($"Resume of {campaignId} did not converge (observed {result.ObservedEffectiveStatus}).");

            op.State = PauseOperationState.Restored;
            op.RestoredAt = now;
        }
    }

    private async Task ExecuteGhlFieldAsync(OutboxAction action, CancellationToken ct)
    {
        var p = JsonSerializer.Deserialize<GhlFieldPayload>(action.PayloadJson)!;
        var result = await ghl.SetContactFieldAsync(p.LocationId, p.ContactId, p.FieldKey, p.Value, ct);
        if (result.Redirected)
            logger.LogInformation("Dunning field-write for action {Id} redirected to the test contact (TestMode).", action.Id);
    }

    private async Task ExecuteGhlTriggerAsync(RdDbContext db, OutboxAction action, DateTimeOffset now, CancellationToken ct)
    {
        var p = JsonSerializer.Deserialize<GhlWorkflowPayload>(action.PayloadJson)!;
        await ghl.TriggerWorkflowAsync(p.LocationId, p.ContactId, p.WorkflowId, ct);

        // Mark the attempt triggered (still unverified — the delivery-verification
        // job confirms an outbound message actually appeared; the dunning window
        // only advances on verified sends).
        var attempt = await db.DunningAttempts
            .FirstOrDefaultAsync(a => a.DunningCaseId == p.DunningCaseId && a.Step == p.Step, ct);
        if (attempt is not null) attempt.TriggeredAt = now;
    }

    private async Task<Outcome> SupersedeAsync(
        RdDbContext db, OutboxAction action, string reason, CancellationToken ct,
        PolicyDecision? racedVerdict = null, Domain.Policy.ClientState? state = null)
    {
        action.Status = OutboxStatus.Superseded;
        action.LastError = Truncate(reason, 2000);
        action.LeaseUntil = null;

        // A pause that flipped to "paid up" is the near-miss the trust metric is
        // about — record it explicitly so the audit shows the engine caught it.
        if (action.ActionType == OutboxActionType.PauseCampaign
            && racedVerdict is { Action: ProposedActionType.None or ProposedActionType.Resume }
            && state is not null)
        {
            db.Decisions.Add(new Decision
            {
                Id = Guid.NewGuid(), ClientId = action.ClientId, EvaluatedAt = clock.UtcNow,
                PolicyVersion = EligibilityPolicy.Version, StateSnapshotJson = JsonSerializer.Serialize(state),
                ProposedAction = racedVerdict.Action, Mode = state.Mode,
                Reason = $"RACE_RECOVERED: a staged pause was superseded because the client is now paid up. {racedVerdict.Reason}",
            });
        }

        await SaveTolerantAsync(db, ct);
        return Outcome.Superseded;
    }

    /// <summary>
    /// Atomic claim: a single UPDATE with locking hints, so two dispatchers can
    /// never lease the same row. READPAST skips rows another worker already
    /// holds; the sequence-group predicate keeps ordered dunning writes in
    /// order (a later step is never claimed while an earlier one is unfinished).
    /// </summary>
    private async Task<List<Guid>> ClaimBatchAsync(RdDbContext db, string owner, DateTimeOffset now, CancellationToken ct)
    {
        const string sql = """
            UPDATE TOP (@batch) o WITH (READPAST, UPDLOCK, ROWLOCK)
            SET Status = 'Leased', LeaseOwner = @owner, FencingToken = @token,
                LeaseUntil = @leaseUntil, NextAttemptAt = NULL, Attempts = Attempts + 1
            OUTPUT inserted.Id AS Value
            FROM OutboxActions AS o
            WHERE o.Status = 'Approved'
              AND (o.NotBefore IS NULL OR o.NotBefore <= @now)
              AND (o.NextAttemptAt IS NULL OR o.NextAttemptAt <= @now)
              AND (o.LeaseUntil IS NULL OR o.LeaseUntil < @now)
              AND (o.SequenceGroup IS NULL OR NOT EXISTS (
                    SELECT 1 FROM OutboxActions AS p
                    WHERE p.SequenceGroup = o.SequenceGroup
                      AND p.SequenceNo < o.SequenceNo
                      AND p.Status NOT IN ('Executed','Superseded','Dismissed')))
            """;

        var leaseUntil = now + _enf.Lease;
        return await db.Database.SqlQueryRaw<Guid>(sql,
                new SqlParameter("@batch", _enf.DispatchBatchSize),
                new SqlParameter("@owner", owner),
                new SqlParameter("@token", clock.UtcNow.Ticks),
                new SqlParameter("@leaseUntil", leaseUntil),
                new SqlParameter("@now", now))
            .ToListAsync(ct);
    }

    /// <summary>Save, treating a lost optimistic-concurrency race as "someone else handled it" rather than a crash.</summary>
    private static async Task SaveTolerantAsync(RdDbContext db, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); }
    }

    private TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Min(300, 15 * Math.Pow(2, Math.Max(0, attempt - 1))));

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
