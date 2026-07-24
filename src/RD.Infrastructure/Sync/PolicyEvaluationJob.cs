using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RD.Domain;
using RD.Domain.Entities;
using RD.Domain.Policy;

namespace RD.Infrastructure.Sync;

/// <summary>
/// The M1 heartbeat: every few minutes, build a ClientState per master-account
/// client from the projections, run the pure policy, and record the verdicts.
///
///   projections ──▶ ClientStateBuilder ──▶ EligibilityPolicy ──▶ Decision log
///                                                     │
///                                                     └▶ InvestigationItems (deduped)
///
/// Shadow phase writes NO OutboxActions — decisions are observation. The
/// dispatcher that turns Assist/Auto verdicts into outbox rows is M2 scope.
///
/// Decision-log volume control: a Decision row is written when the verdict
/// proposes an action/investigation OR when the verdict CHANGED from the
/// client's previous decision (so recoveries to "None" are visible and clean
/// days remain computable) — steady-state "nothing to do" is not re-logged
/// every 5 minutes.
/// </summary>
public sealed class PolicyEvaluationJob(
    IDbContextFactory<RdDbContext> dbFactory,
    IClock clock,
    ILogger<PolicyEvaluationJob> logger)
{
    public async Task RunAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = clock.UtcNow;

        var stripeSyncedAt = await LatestCompleted(db, ExternalSystem.Stripe, ct);
        var metaSyncedAt = await LatestCompleted(db, ExternalSystem.Meta, ct);

        var clients = await db.Clients.AsNoTracking()
            .Where(c => c.AccountType == AccountType.Master)
            .ToListAsync(ct);

        var links = await db.IdentityLinks.AsNoTracking()
            .Where(l => l.InvalidatedAt == null)
            .ToListAsync(ct);
        var linksByClient = links.ToLookup(l => l.ClientId);

        var subs = await db.StripeSubscriptions.AsNoTracking().ToListAsync(ct);
        var subsById = subs.ToDictionary(s => s.SubscriptionId);
        var openInvoices = await db.StripeInvoices.AsNoTracking()
            .Where(i => i.Status == "open" || i.Status == "uncollectible")
            .ToListAsync(ct);
        var invoicesByClient = openInvoices.Where(i => i.ClientId != null).ToLookup(i => i.ClientId!.Value);

        var campaigns = await db.MetaCampaigns.AsNoTracking().ToListAsync(ct);
        var campaignsById = campaigns.ToDictionary(c => c.CampaignId);

        var appPausedIds = await db.PauseOperations.AsNoTracking()
            .Where(p => p.State == PauseOperationState.Paused)
            .Select(p => p.ExternalId)
            .ToListAsync(ct);
        var appPausedSet = appPausedIds.ToHashSet();

        var openCases = await db.DunningCases.AsNoTracking()
            .Where(c => c.Status == DunningCaseStatus.Open)
            .Include(c => c.Attempts)
            .ToListAsync(ct);
        var casesByClient = openCases.ToLookup(c => c.ClientId);

        var trials = await db.TrialPeriods.AsNoTracking()
            .Where(t => t.Outcome == TrialOutcome.Active)
            .ToListAsync(ct);
        var trialsByClient = trials.ToLookup(t => t.ClientId);

        var ledgerAgg = await db.LedgerEntries.AsNoTracking()
            .GroupBy(l => new { l.ClientId, l.Type })
            .Select(g => new { g.Key.ClientId, g.Key.Type, Sum = g.Sum(x => x.SignedAmount) })
            .ToListAsync(ct);
        var ledgerByClient = ledgerAgg.ToLookup(x => x.ClientId);

        var latestDecisions = await db.Decisions.AsNoTracking()
            .GroupBy(d => d.ClientId)
            .Select(g => g.OrderByDescending(d => d.EvaluatedAt).First())
            .ToListAsync(ct);
        var latestByClient = latestDecisions.ToDictionary(d => d.ClientId);

        var openInvestigations = await db.InvestigationItems.AsNoTracking()
            .Where(i => i.Status == InvestigationStatus.Open && i.ClientId != null)
            .Select(i => new { i.ClientId, i.Kind })
            .ToListAsync(ct);
        var openInvestigationSet = openInvestigations.Select(i => (i.ClientId!.Value, i.Kind)).ToHashSet();

        var evaluated = 0; var logged = 0; var investigationsCreated = 0;

        foreach (var client in clients)
        {
            ct.ThrowIfCancellationRequested();
            var clientLinks = linksByClient[client.Id].ToList();

            var subLink = clientLinks.FirstOrDefault(l => l is { System: ExternalSystem.Stripe, Kind: LinkKind.Subscription });
            var sub = subLink is not null && subsById.TryGetValue(subLink.ExternalId, out var sp) ? sp : null;

            var campaignStates = clientLinks
                .Where(l => l is { System: ExternalSystem.Meta, Kind: LinkKind.Campaign })
                .Select(l => campaignsById.TryGetValue(l.ExternalId, out var cp)
                    ? new CampaignState(cp.CampaignId, cp.EffectiveStatus, appPausedSet.Contains(cp.CampaignId))
                    : null)
                .Where(c => c is not null).Select(c => c!)
                .ToList();

            // MappingVerified here means "the required links exist and are live" —
            // structural completeness. Human sign-off (MappingVerification rows +
            // blast-radius ack) gates ladder PROMOTION, not shadow evaluation;
            // drift invalidates links, which flips this back to false.
            var mappingComplete =
                clientLinks.Any(l => l is { System: ExternalSystem.Stripe, Kind: LinkKind.Customer }) &&
                subLink is not null &&
                clientLinks.Any(l => l is { System: ExternalSystem.Meta, Kind: LinkKind.Campaign }) &&
                clientLinks.Any(l => l is { System: ExternalSystem.Ghl, Kind: LinkKind.Contact });

            var trial = trialsByClient[client.Id].OrderByDescending(t => t.StartsAt).FirstOrDefault();
            var dunningCase = casesByClient[client.Id].OrderByDescending(c => c.OpenedAt).FirstOrDefault();

            var ledger = ledgerByClient[client.Id].ToDictionary(x => x.Type, x => x.Sum);
            var adSpend = -ledger.GetValueOrDefault(LedgerEntryType.AdSpend); // stored negative
            var paid = ledger.GetValueOrDefault(LedgerEntryType.ChargePaid);
            var exposure = Math.Max(0m, adSpend - paid);

            var clientOpenInvoices = invoicesByClient[client.Id].ToList();

            var state = new ClientState
            {
                ClientId = client.Id,
                Mode = client.EnforcementMode,
                Contract = client.ContractType,
                Account = client.AccountType,
                CurrencyCode = client.CurrencyCode,
                MappingVerified = mappingComplete,
                HasActiveTrial = trial is not null,
                TrialExpiresAt = trial?.ExpiresAt,
                TrialSpend = trial is null ? 0m : adSpend,
                TrialSpendCap = trial?.SpendCapSnapshot,
                SubscriptionStatus = sub?.Status,
                OpenUnpaidInvoices = clientOpenInvoices.Count(i => i.DueDate < now || i.Status == "uncollectible"),
                PaymentReceivedForCanceledSub = false, // webhook-era signal (M2)
                Dunning = dunningCase is null ? null : ToDunningState(dunningCase),
                HasNewFailedCharge = dunningCase is null && clientOpenInvoices.Any(i => i.Status == "uncollectible"),
                Campaigns = campaignStates,
                Exposure = exposure,
                StripeSyncedAt = stripeSyncedAt,
                MetaSyncedAt = metaSyncedAt,
                EvaluatedAt = now,
            };

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
                    Id = Guid.NewGuid(),
                    ClientId = client.Id,
                    Kind = kind,
                    Detail = verdict.Reason,
                    CreatedAt = now,
                });
                investigationsCreated++;
            }
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Policy evaluation: {Evaluated} master clients evaluated, {Logged} decisions logged, {Investigations} new investigations.",
            evaluated, logged, investigationsCreated);
    }

    private static DunningState ToDunningState(DunningCase c)
    {
        var attempts = c.Attempts.OrderBy(a => a.Step).ToList();
        var triggered = attempts.Where(a => a.TriggeredAt != null).ToList();
        return new DunningState(
            LastStep: triggered.Count == 0 ? 0 : triggered.Max(a => a.Step),
            WindowExpiresAt: c.WindowExpiresAt,
            NextStepDueAt: attempts.FirstOrDefault(a => a.TriggeredAt == null)?.DueAt,
            AllSendsVerified: triggered.All(a => a.VerifiedAt != null),
            OldestUnverifiedSince: triggered.Where(a => a.VerifiedAt == null).Min(a => (DateTimeOffset?)a.TriggeredAt));
    }

    private static async Task<DateTimeOffset?> LatestCompleted(RdDbContext db, ExternalSystem system, CancellationToken ct) =>
        await db.SyncRuns.AsNoTracking()
            .Where(r => r.System == system && r.Status == SyncRunStatus.Completed)
            .MaxAsync(r => (DateTimeOffset?)r.CompletedAt, ct);
}
