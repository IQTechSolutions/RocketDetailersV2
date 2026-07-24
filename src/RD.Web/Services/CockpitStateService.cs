using Microsoft.EntityFrameworkCore;
using RD.Domain;
using RD.Infrastructure;

namespace RD.Web.Services;

/// <summary>
/// Loads cockpit inputs through the context factory (Blazor circuits are
/// long-lived — never inject RdDbContext directly) and hands them to the pure
/// <see cref="CockpitRules"/> for derivation.
/// </summary>
public class CockpitStateService(IDbContextFactory<RdDbContext> factory, IClock clock)
{
    public async Task<CockpitSnapshot> GetSnapshotAsync(CancellationToken ct = default)
        => CockpitRules.Compute(await LoadAsync(ct), clock.UtcNow);

    public async Task<CockpitData> LoadAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        // Retired duplicates (merged into another account) aren't live clients — exclude
        // them from every live reporting surface (headcounts AND the cash roll-up below).
        // Their ledger still counts where it matters: the survivor's enforcement balance
        // rolls it up (ClientStateBuilder), so no one is wrongly paused — it just doesn't
        // show as a separate line in the top-level cockpit tiles.
        var totalClients = await db.Clients.CountAsync(c => c.MergedIntoClientId == null, ct);
        var masterCount = await db.Clients.CountAsync(c => c.AccountType == AccountType.Master && c.MergedIntoClientId == null, ct);

        var clientsLinked = await db.IdentityLinks
            .Where(l => l.System == ExternalSystem.Stripe && l.Kind == LinkKind.Subscription && l.InvalidatedAt == null)
            .Select(l => l.ClientId)
            .Distinct()
            .CountAsync(ct);

        var campaignsLive = await db.MetaCampaigns
            .Where(m => m.EffectiveStatus == "ACTIVE" && m.ClientId != null)
            .Join(db.Clients.Where(c => c.AccountType == AccountType.Master && c.MergedIntoClientId == null),
                m => m.ClientId, c => c.Id, (m, c) => c.Id)
            .Distinct()
            .CountAsync(ct);

        var openActions = await db.OutboxActions
            .CountAsync(a => a.Status == OutboxStatus.Pending || a.Status == OutboxStatus.AwaitingApproval, ct);

        // Shadow phase: the latest would-X verdicts are the queue. (M2's
        // dispatcher stages Assist/Auto verdicts as OutboxActions; shadow
        // clients keep appearing here, exactly like the approved wireframe.)
        // NOTE: composing Where/Count AFTER GroupBy+First() trips an EF
        // translation bug (EmptyProjectionMember) — materialize, then filter.
        var latestVerdicts = await db.Decisions
            .GroupBy(d => d.ClientId)
            .Select(g => g.OrderByDescending(d => d.EvaluatedAt).First())
            .ToListAsync(ct);
        var shadowVerdicts = latestVerdicts.Count(d => d.ProposedAction
            is ProposedActionType.Pause or ProposedActionType.Resume
            or ProposedActionType.DunningStep or ProposedActionType.Escalate);

        // Master-client ledger facts, rolled up in memory by CockpitRules (v1: hundreds of clients, fine).
        var ledger = await db.LedgerEntries
            .Join(db.Clients.Where(c => c.AccountType == AccountType.Master && c.MergedIntoClientId == null),
                l => l.ClientId, c => c.Id,
                (l, c) => new LedgerFact(l.ClientId, c.CurrencyCode, l.Type, l.SignedAmount, l.OccurredAt))
            .ToListAsync(ct);

        var syncs = await db.SyncRuns
            .Where(s => s.Status == SyncRunStatus.Completed && s.CompletedAt != null)
            .GroupBy(s => s.System)
            .Select(g => new CompletedSyncFact(g.Key, g.Max(s => s.CompletedAt!.Value)))
            .ToListAsync(ct);

        var gaps = await db.InvestigationItems
            .Where(i => i.Status == InvestigationStatus.Open)
            .GroupBy(i => i.Kind)
            .Select(g => new MappingGap(g.Key, g.Count()))
            .ToListAsync(ct);

        return new CockpitData
        {
            TotalClients = totalClients,
            MasterClientCount = masterCount,
            ClientsWithActiveStripeSubscription = clientsLinked,
            CampaignsLiveMasterClients = campaignsLive,
            OpenActionCount = openActions,
            ShadowVerdictCount = shadowVerdicts,
            MasterClientLedger = ledger,
            CompletedSyncRuns = syncs,
            OpenInvestigations = gaps,
        };
    }
}
