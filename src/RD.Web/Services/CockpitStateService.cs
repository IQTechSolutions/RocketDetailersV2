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

        var totalClients = await db.Clients.CountAsync(ct);
        var masterCount = await db.Clients.CountAsync(c => c.AccountType == AccountType.Master, ct);

        var clientsLinked = await db.IdentityLinks
            .Where(l => l.System == ExternalSystem.Stripe && l.Kind == LinkKind.Subscription && l.InvalidatedAt == null)
            .Select(l => l.ClientId)
            .Distinct()
            .CountAsync(ct);

        var campaignsLive = await db.MetaCampaigns
            .Where(m => m.EffectiveStatus == "ACTIVE" && m.ClientId != null)
            .Join(db.Clients.Where(c => c.AccountType == AccountType.Master),
                m => m.ClientId, c => c.Id, (m, c) => c.Id)
            .Distinct()
            .CountAsync(ct);

        var openActions = await db.OutboxActions
            .CountAsync(a => a.Status == OutboxStatus.Pending || a.Status == OutboxStatus.AwaitingApproval, ct);

        // Master-client ledger facts, rolled up in memory by CockpitRules (v1: hundreds of clients, fine).
        var ledger = await db.LedgerEntries
            .Join(db.Clients.Where(c => c.AccountType == AccountType.Master),
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
            MasterClientLedger = ledger,
            CompletedSyncRuns = syncs,
            OpenInvestigations = gaps,
        };
    }
}
