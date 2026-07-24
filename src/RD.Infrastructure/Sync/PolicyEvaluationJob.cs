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
/// clients — stages OutboxActions. Shadow clients only get a decision-log entry;
/// their would-X verdicts drive the cockpit queue with no side effects.
///
///   Client ──▶ ClientStateBuilder ──▶ EligibilityPolicy ──┬─▶ Decision (log)
///                                                          ├─▶ InvestigationItem (deduped)
///                                                          ├─▶ demote to Shadow (on drift)
///                                                          └─▶ ActionStager (Assist/Auto only)
///
/// Decision logging is change-driven: a row is written when the verdict
/// proposes an action/investigation OR the verdict changed from last time, so
/// steady-state "nothing to do" is not re-logged every cycle.
/// </summary>
public sealed class PolicyEvaluationJob(
    IDbContextFactory<RdDbContext> dbFactory,
    ClientStateBuilder stateBuilder,
    ActionStager stager,
    IClock clock,
    ILogger<PolicyEvaluationJob> logger)
{
    public async Task RunAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = clock.UtcNow;
        var ctx = await stateBuilder.LoadContextAsync(db, ct);

        var killSwitch = await GetOrCreateKillSwitchAsync(db, now, ct);

        var clients = await db.Clients.AsNoTracking()
            .Where(c => c.AccountType == AccountType.Master)
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

        var evaluated = 0; var logged = 0; var investigationsCreated = 0; var staged = 0; var demoted = 0;
        var demoteIds = new List<Guid>();

        foreach (var client in clients)
        {
            ct.ThrowIfCancellationRequested();

            var state = await stateBuilder.BuildAsync(db, client, ctx, ct);
            var verdict = EligibilityPolicy.Evaluate(state);
            evaluated++;

            var previous = latestByClient.GetValueOrDefault(client.Id);
            var changed = previous is null
                || previous.ProposedAction != verdict.Action
                || previous.Reason != verdict.Reason;

            if (verdict.Action != ProposedActionType.None || changed)
            {
                db.Decisions.Add(new Decision
                {
                    Id = Guid.NewGuid(),
                    ClientId = client.Id,
                    EvaluatedAt = now,
                    PolicyVersion = EligibilityPolicy.Version,
                    StateSnapshotJson = JsonSerializer.Serialize(state),
                    ProposedAction = verdict.Action,
                    Mode = client.EnforcementMode,
                    Reason = verdict.Reason,
                });
                logged++;
            }

            if (verdict.Investigation is { } kind && openInvestigationSet.Add((client.Id, kind)))
            {
                db.InvestigationItems.Add(new InvestigationItem
                {
                    Id = Guid.NewGuid(), ClientId = client.Id, Kind = kind, Detail = verdict.Reason, CreatedAt = now,
                });
                investigationsCreated++;
            }

            if (verdict.DemoteToShadow && client.EnforcementMode != EnforcementMode.Shadow)
            {
                demoteIds.Add(client.Id);
                demoted++;
                continue; // demoted clients don't stage this cycle
            }

            // Kill switch halts all staging; the client is (or will be) reverted to Shadow.
            if (!killSwitch.Enabled)
            {
                var before = db.ChangeTracker.Entries<OutboxAction>().Count();
                await stager.StageAsync(db, client, state, verdict, killSwitch.Epoch, ct);
                staged += db.ChangeTracker.Entries<OutboxAction>().Count() - before;
            }
        }

        await db.SaveChangesAsync(ct);

        if (demoteIds.Count > 0)
        {
            await db.Clients.Where(c => demoteIds.Contains(c.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.EnforcementMode, EnforcementMode.Shadow), ct);
        }

        logger.LogInformation(
            "Policy evaluation: {Evaluated} evaluated, {Logged} decisions, {Investigations} new investigations, {Staged} actions staged, {Demoted} demoted to Shadow.",
            evaluated, logged, investigationsCreated, staged, demoted);
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
