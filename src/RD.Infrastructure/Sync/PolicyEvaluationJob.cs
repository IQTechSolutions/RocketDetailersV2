using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RD.Domain;
using RD.Domain.Entities;
using RD.Domain.Policy;
using RD.Infrastructure.Enforcement;

namespace RD.Infrastructure.Sync;

/// <summary>
/// The enforcement heartbeat. Every few minutes it builds a ClientState per
/// master client (via the shared <see cref="ClientStateBuilder"/> the dispatcher
/// also uses), runs the pure policy, records the verdict, and — for Assist/Auto
/// clients — stages OutboxActions. Shadow clients only get V2 audit entries;
/// their would-X verdicts drive the cockpit queue with no provider side effects.
///
///   Client ──▶ ClientStateBuilder ──▶ EligibilityPolicy ──┬─▶ Decision (log)
///                                                          ├─▶ InvestigationItem (deduped)
///                                                          ├─▶ demote to Shadow (on drift)
///                                                          └─▶ ActionStager (Assist/Auto only)
///
/// Decision logging is change-driven: a row is written only when the policy,
/// mode, verdict, reason, or exact target set changes. Repeated heartbeats for
/// the same recommendation do not inflate the audit log.
/// </summary>
public sealed class PolicyEvaluationJob(
    IDbContextFactory<RdDbContext> dbFactory,
    ClientStateBuilder stateBuilder,
    ActionStager stager,
    MetaShadowPredictionRecorder predictionRecorder,
    IClock clock,
    ILogger<PolicyEvaluationJob> logger)
{
    private const string AutoResolutionActor = "policy-evaluation";

    public async Task RunAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = clock.UtcNow;
        var ctx = await stateBuilder.LoadContextAsync(db, ct);

        var killSwitch = await GetOrCreateKillSwitchAsync(db, now, ct);

        // Retired duplicates (merged into another account) are inert — their links
        // and future movement resolve to the survivor, so never evaluate the shell.
        var clients = await db.Clients.AsNoTracking()
            .Where(c => c.AccountType == AccountType.Master && c.MergedIntoClientId == null)
            .ToListAsync(ct);

        var latestByClient = (await db.Decisions.AsNoTracking()
                .GroupBy(d => d.ClientId)
                .Select(g => g.OrderByDescending(d => d.EvaluatedAt).First())
                .ToListAsync(ct))
            .ToDictionary(d => d.ClientId);

        var openInvestigationSet = (await db.InvestigationItems.AsNoTracking()
                .Where(i => i.Status == InvestigationStatus.Open && i.ClientId != null)
                .Select(i => new { i.ClientId, i.Kind })
                .ToListAsync(ct))
            .Select(i => (i.ClientId!.Value, i.Kind)).ToHashSet();

        // InvestigationItem has no durable source/provenance column yet. Limit
        // automatic lifecycle management to the exact detail emitted by this
        // policy branch. Filter the detail in memory with ordinal comparison so
        // SQL Server's case-insensitive collation cannot broaden the fingerprint.
        var externalPauseCandidates = await db.InvestigationItems.AsNoTracking()
            .Where(i => i.Status == InvestigationStatus.Open
                        && i.ClientId != null
                        && i.Kind == InvestigationKind.ExternallyPausedPayment)
            .Select(i => new { i.Id, i.ClientId, i.Detail })
            .ToListAsync(ct);
        var openPolicyExternalPausesByClient = externalPauseCandidates
            .Where(i => string.Equals(
                i.Detail,
                EligibilityPolicy.ExternallyPausedPaymentReason,
                StringComparison.Ordinal))
            .GroupBy(i => i.ClientId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(i => i.Id).ToArray());

        var evaluated = 0; var logged = 0; var investigationsCreated = 0; var investigationsResolved = 0; var staged = 0; var demoted = 0;

        foreach (var clientSnapshot in clients)
        {
            ct.ThrowIfCancellationRequested();

            // Mapping, merge, billing, and dispatch all share this per-client
            // fence. Re-read the client after winning it and keep it through the
            // final action insert so a demotion cannot miss a stale Auto action
            // that was still only tracked in this job.
            await using var mutationFence = await ClientMutationFence.AcquireAsync(db, clientSnapshot.Id, ct);
            var client = await db.Clients.FirstOrDefaultAsync(c =>
                c.Id == clientSnapshot.Id
                && c.AccountType == AccountType.Master
                && c.MergedIntoClientId == null, ct);
            if (client is null) continue;

            var state = await stateBuilder.BuildAsync(db, client, ctx, ct);
            var verdict = EligibilityPolicy.Evaluate(state);
            evaluated++;

            if (openPolicyExternalPausesByClient.TryGetValue(client.Id, out var externalPauseIds)
                && await GetExternalPauseResolutionNoteAsync(db, client.Id, state, ct) is { } resolutionNote)
            {
                // Compare-and-set: an operator may resolve/dismiss after the IDs
                // were read. Only untouched Open rows are ours to auto-resolve.
                investigationsResolved += await db.InvestigationItems
                    .Where(i => externalPauseIds.Contains(i.Id)
                                && i.Status == InvestigationStatus.Open
                                && i.ResolvedAt == null
                                && i.ResolvedBy == null
                                && i.ResolutionNote == null)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(i => i.Status, InvestigationStatus.Resolved)
                        .SetProperty(i => i.ResolvedAt, now)
                        .SetProperty(i => i.ResolvedBy, AutoResolutionActor)
                        .SetProperty(i => i.ResolutionNote, resolutionNote), ct);
            }

            var previous = latestByClient.GetValueOrDefault(client.Id);
            var targetCampaignIdsJson = JsonSerializer.Serialize(
                (verdict.TargetCampaignIds ?? [])
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal));
            var changed = previous is null
                || previous.PolicyVersion != EligibilityPolicy.Version
                || previous.ProposedAction != verdict.Action
                || previous.Mode != client.EnforcementMode
                || previous.Reason != verdict.Reason
                || previous.TargetCampaignIdsJson != targetCampaignIdsJson;

            Decision decision;
            if (changed)
            {
                decision = new Decision
                {
                    Id = Guid.NewGuid(),
                    ClientId = client.Id,
                    EvaluatedAt = now,
                    PolicyVersion = EligibilityPolicy.Version,
                    StateSnapshotJson = JsonSerializer.Serialize(state),
                    ProposedAction = verdict.Action,
                    Mode = client.EnforcementMode,
                    TargetCampaignIdsJson = targetCampaignIdsJson,
                    Reason = verdict.Reason,
                };
                db.Decisions.Add(decision);
                logged++;
            }
            else
            {
                decision = previous!;
            }

            await predictionRecorder.RecordAsync(
                db,
                client,
                state,
                verdict,
                decision.Id,
                now,
                ct);

            if (verdict.Investigation is { } kind && openInvestigationSet.Add((client.Id, kind)))
            {
                db.InvestigationItems.Add(new InvestigationItem
                {
                    Id = Guid.NewGuid(), ClientId = client.Id, Kind = kind, Detail = verdict.Reason, CreatedAt = now,
                });
                investigationsCreated++;
            }

            if (verdict.DemoteToShadow)
            {
                if (client.EnforcementMode != EnforcementMode.Shadow)
                {
                    client.EnforcementMode = EnforcementMode.Shadow;
                    demoted++;
                }

                var queuedActions = await db.OutboxActions
                    .Where(action => action.ClientId == client.Id
                                     && (action.Status == OutboxStatus.Pending
                                         || action.Status == OutboxStatus.AwaitingApproval
                                         || action.Status == OutboxStatus.Approved
                                         || action.Status == OutboxStatus.Leased
                                         || action.Status == OutboxStatus.Failed))
                    .ToListAsync(ct);
                foreach (var action in queuedActions)
                {
                    action.Status = OutboxStatus.Superseded;
                    action.ActionVersion++;
                    action.LeaseOwner = null;
                    action.FencingToken = null;
                    action.LeaseUntil = null;
                    action.NextAttemptAt = null;
                    action.LastError =
                        "Superseded because policy demoted the client to Shadow; re-evaluate after the client is safely promoted again.";
                }

                await db.SaveChangesAsync(ct);
                continue; // demoted clients don't stage this cycle
            }

            // Kill switch halts all staging; the client is (or will be) reverted to Shadow.
            if (!killSwitch.Enabled)
            {
                var before = db.ChangeTracker.Entries<OutboxAction>().Count();
                await stager.StageAsync(db, client, state, verdict, killSwitch.Epoch, ct);
                staged += db.ChangeTracker.Entries<OutboxAction>().Count() - before;
            }

            await db.SaveChangesAsync(ct);
        }

        logger.LogInformation(
            "Policy evaluation: {Evaluated} evaluated, {Logged} decisions, {Investigations} new investigations, {InvestigationsResolved} investigations auto-resolved, {Staged} actions staged, {Demoted} demoted to Shadow.",
            evaluated, logged, investigationsCreated, investigationsResolved, staged, demoted);
    }

    /// <summary>
    /// Returns an audit note only when fresh vendor evidence disproves one of the
    /// two facts behind ExternallyPausedPayment: (a) a linked campaign is still
    /// externally paused and (b) the client is still paid up. A different policy
    /// verdict is not evidence because higher-priority rules can mask (a).
    /// </summary>
    private static async Task<string?> GetExternalPauseResolutionNoteAsync(
        RdDbContext db,
        Guid clientId,
        ClientState state,
        CancellationToken ct)
    {
        if (!IsFresh(state.MetaSyncedAt, state.EvaluatedAt))
            return null;

        var linkedCampaignIds = await db.IdentityLinks.AsNoTracking()
            .Where(l => l.ClientId == clientId
                        && l.System == ExternalSystem.Meta
                        && l.Kind == LinkKind.Campaign
                        && l.InvalidatedAt == null)
            .Select(l => l.ExternalId)
            .Distinct()
            .ToListAsync(ct);
        if (linkedCampaignIds.Count == 0)
            return null;

        var campaigns = await db.MetaCampaigns.AsNoTracking()
            .Where(c => linkedCampaignIds.Contains(c.CampaignId))
            .Select(c => new { c.CampaignId, c.EffectiveStatus, c.SourceSyncedAt })
            .ToListAsync(ct);
        // A missing projection is unknown, not proof that a pause disappeared.
        if (campaigns.Count != linkedCampaignIds.Count
            || campaigns.Any(c => !IsFresh(c.SourceSyncedAt, state.EvaluatedAt)))
            return null;

        var appPausedCampaignIds = (await db.PauseOperations.AsNoTracking()
                .Where(p => p.ClientId == clientId
                            && p.EntityType == MetaEntityType.Campaign
                            && p.State == PauseOperationState.Paused)
                .Select(p => p.ExternalId)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        var hasExternalPause = campaigns.Any(c =>
            (c.EffectiveStatus is "PAUSED" or "CAMPAIGN_PAUSED")
            && !appPausedCampaignIds.Contains(c.CampaignId));

        if (!hasExternalPause)
            return "Auto-resolved from fresh Meta evidence: no linked campaign is still externally paused.";

        // A still-paused campaign only makes this investigation stale when fresh
        // Stripe evidence disproves the original paid-up premise.
        if (state.SubscriptionStatus is not ("unpaid" or "canceled")
            || !IsFresh(state.StripeSyncedAt, state.EvaluatedAt))
            return null;

        var linkedSubscriptionIds = await db.IdentityLinks.AsNoTracking()
            .Where(l => l.ClientId == clientId
                        && l.System == ExternalSystem.Stripe
                        && l.Kind == LinkKind.Subscription
                        && l.InvalidatedAt == null)
            .Select(l => l.ExternalId)
            .Distinct()
            .ToListAsync(ct);
        if (linkedSubscriptionIds.Count == 0)
            return null;

        var subscriptions = await db.StripeSubscriptions.AsNoTracking()
            .Where(s => linkedSubscriptionIds.Contains(s.SubscriptionId))
            .Select(s => new { s.Status, s.SourceSyncedAt })
            .ToListAsync(ct);
        if (subscriptions.Count != linkedSubscriptionIds.Count
            || subscriptions.Any(s => !IsFresh(s.SourceSyncedAt, state.EvaluatedAt))
            || ClientStateBuilder.BestSubscriptionStatus(subscriptions.Select(s => s.Status)) != state.SubscriptionStatus)
            return null;

        return $"Auto-resolved from fresh Stripe and Meta evidence: subscription status is {state.SubscriptionStatus}, so the prior paid-up external-pause condition no longer applies.";
    }

    private static bool IsFresh(DateTimeOffset? syncedAt, DateTimeOffset evaluatedAt)
    {
        if (syncedAt is not { } observedAt) return false;
        var age = evaluatedAt - observedAt;
        return age >= TimeSpan.Zero && age <= EligibilityPolicy.StalenessBound;
    }

    private static async Task<KillSwitchState> GetOrCreateKillSwitchAsync(RdDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var ks = await db.KillSwitch.FirstOrDefaultAsync(k => k.Id == 1, ct);
        if (ks is not null) return ks;
        ks = new KillSwitchState { Id = 1, Enabled = false, Epoch = 0, ChangedAt = now };
        db.KillSwitch.Add(ks);
        await db.SaveChangesAsync(ct);
        return ks;
    }
}
