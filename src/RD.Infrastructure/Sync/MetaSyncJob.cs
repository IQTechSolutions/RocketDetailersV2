using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RD.Domain;
using RD.Domain.Entities;
using RD.Infrastructure.Gateways;

namespace RD.Infrastructure.Sync;

/// <summary>
/// Full-sweep Meta sync for the configured master ad account: upserts campaign
/// projections (RemoteVersion = updated_time, the read-back convergence
/// evidence), sweeps yesterday's + today's daily insights, and ingests AdSpend
/// into the append-only ledger. Same complete-sweep SyncRun semantics as the
/// Stripe job.
/// </summary>
public sealed class MetaSyncJob(
    IDbContextFactory<RdDbContext> dbFactory,
    IMetaAdsGateway meta,
    IOptions<MetaOptions> options,
    IClock clock,
    ILogger<MetaSyncJob> logger)
{
    [DisableConcurrentExecution("rd:meta-sync", 30 * 60)]
    public async Task RunAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var run = new SyncRun { System = ExternalSystem.Meta, StartedAt = clock.UtcNow };
        db.SyncRuns.Add(run);
        await db.SaveChangesAsync(ct);

        try
        {
            run.ItemsSeen = await SweepAsync(db, ct);
            run.Status = SyncRunStatus.Completed;
            run.CompletedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Meta sync sweep failed; SyncRun {SyncRunId} marked Failed", run.Id);
            await SyncUtil.RecordFailureAsync(db, run, ex, logger);
        }
    }

    private async Task<int> SweepAsync(RdDbContext db, CancellationToken ct)
    {
        var o = options.Value;
        var now = clock.UtcNow;
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        // Window is configurable: default 1 = yesterday+today (cheap, for the recurring
        // job); a larger InsightsLookbackDays powers a one-off spend backfill.
        var since = today.AddDays(-Math.Max(1, o.InsightsLookbackDays));

        var campaignToClient = (await db.IdentityLinks
                .Where(l => l.System == ExternalSystem.Meta && l.Kind == LinkKind.Campaign && l.InvalidatedAt == null)
                .Select(l => new { l.ExternalId, l.ClientId })
                .ToListAsync(ct))
            .ToDictionary(l => l.ExternalId, l => l.ClientId);

        // --- Campaigns: complete cursor-paginated sweep, then upsert.
        var campaigns = await meta.ListCampaignsAsync(o.AdAccountId, ct);
        var campaignProjections = await db.MetaCampaigns.ToDictionaryAsync(p => p.CampaignId, ct);
        foreach (var campaign in campaigns)
        {
            if (!campaignProjections.TryGetValue(campaign.Id, out var proj))
            {
                proj = new MetaCampaignProj
                {
                    CampaignId = campaign.Id,
                    AdAccountId = o.AdAccountId,
                    Status = campaign.Status,
                    EffectiveStatus = campaign.EffectiveStatus,
                };
                campaignProjections[campaign.Id] = proj;
                db.MetaCampaigns.Add(proj);
            }
            proj.AdAccountId = o.AdAccountId;
            proj.Name = campaign.Name;
            proj.Status = campaign.Status;
            proj.EffectiveStatus = campaign.EffectiveStatus;
            proj.DailyBudget = campaign.DailyBudget;
            proj.RemoteVersion = campaign.UpdatedTime;
            proj.ClientId = campaignToClient.TryGetValue(campaign.Id, out var clientId) ? clientId : null;
            proj.SourceSyncedAt = now;
        }

        // --- Insights: [since .. today], daily granularity.
        var insights = await meta.GetDailyInsightsAsync(o.AdAccountId, since, today, ct);
        var insightProjections = await db.MetaInsightsDaily
            .Where(i => i.Date >= since)
            .ToDictionaryAsync(i => (i.CampaignId, i.Date), ct);
        foreach (var insight in insights)
        {
            if (!insightProjections.TryGetValue((insight.CampaignId, insight.Date), out var proj))
            {
                proj = new MetaInsightDailyProj { CampaignId = insight.CampaignId, Date = insight.Date };
                insightProjections[(insight.CampaignId, insight.Date)] = proj;
                db.MetaInsightsDaily.Add(proj);
            }
            proj.Spend = insight.Spend;
            proj.CurrencyCode = o.AccountCurrency;
            proj.Clicks = insight.Clicks;
            proj.Leads = insight.LeadActions;
            proj.ClientId = campaignToClient.TryGetValue(insight.CampaignId, out var clientId) ? clientId : null;
            proj.SourceSyncedAt = now;
        }

        await db.SaveChangesAsync(ct);

        // --- AdSpend ledger ingestion. Idempotency key = campaignId:date, so the
        // FIRST observation of a campaign-date wins. For a still-running day that
        // means spend-so-far; the projection above always carries the latest
        // number, and exposure reconciliation (M2) owns issuing Adjustment
        // entries where the final figure drifts from the first-observed one.
        // Append-only means we correct by compensation, never by update.
        var candidates = new List<LedgerEntry>();
        foreach (var insight in insights)
        {
            if (insight.Spend <= 0m) continue; // zero-spend days carry no money movement
            if (!campaignToClient.TryGetValue(insight.CampaignId, out var clientId)) continue;

            candidates.Add(new LedgerEntry
            {
                ClientId = clientId,
                OccurredAt = new DateTimeOffset(insight.Date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
                RecordedAt = now,
                Type = LedgerEntryType.AdSpend,
                SignedAmount = -insight.Spend, // money out → negative
                CurrencyCode = o.AccountCurrency,
                SourceSystem = ExternalSystem.Meta,
                SourceObjectId = $"{insight.CampaignId}:{insight.Date:yyyy-MM-dd}",
            });
        }

        await LedgerIngest.InsertIdempotentAsync(db, candidates, ct);
        return campaigns.Count + insights.Count;
    }
}
