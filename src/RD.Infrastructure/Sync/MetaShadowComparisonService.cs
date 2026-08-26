using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RD.Domain;
using RD.Domain.Entities;
using RD.Domain.Policy;
using RD.Infrastructure.Gateways;

namespace RD.Infrastructure.Sync;

/// <summary>
/// Pulls Meta's campaign-status audit trail through the GET-only reader, stores
/// immutable facts in V2, and computes a rolling shadow report. This service is
/// deliberately not registered as a recurring job.
/// </summary>
public sealed class MetaShadowComparisonService(
    IDbContextFactory<RdDbContext> dbFactory,
    IMetaActivityReader activityReader,
    IOptions<MetaOptions> metaOptions,
    IOptions<MetaShadowComparisonOptions> comparisonOptions,
    IClock clock,
    ILogger<MetaShadowComparisonService> logger)
{
    public async Task<MetaShadowComparisonReport> SyncAndCompareAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var options = comparisonOptions.Value;
        var reportFrom = now.AddDays(-Math.Max(1, options.ActivityLookbackDays));
        var configuredAdAccountId = metaOptions.Value.AdAccountId;
        if (string.IsNullOrWhiteSpace(configuredAdAccountId))
            throw new InvalidOperationException("Meta AdAccountId is not configured for shadow comparison.");
        var adAccountId = NormalizeActId(configuredAdAccountId);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var latestEvent = await db.MetaActivityFacts
            .MaxAsync(fact => (DateTimeOffset?)fact.EventTime, ct);
        var overlap = TimeSpan.FromHours(Math.Max(1, options.ActivityOverlapHours));
        var readFrom = latestEvent is null || latestEvent.Value - overlap < reportFrom
            ? reportFrom
            : latestEvent.Value - overlap;

        var fetched = await activityReader.ListCampaignStatusActivitiesAsync(
            adAccountId,
            readFrom,
            now,
            ct);
        var inserted = await InsertFactsAsync(db, adAccountId, fetched, now, ct);
        var report = await BuildReportAsync(
            db,
            reportFrom,
            now,
            TimeSpan.FromHours(Math.Max(1, options.MatchWindowHours)),
            ct);

        logger.LogInformation(
            "Meta shadow comparison: {Fetched} activities fetched, {Inserted} new facts, {Matched}/{Scored} predictions matched, {Covered}/{Actual} actual actions covered.",
            fetched.Count,
            inserted,
            report.Metrics.MatchedPredictions,
            report.Metrics.ScoredPredictions,
            report.Metrics.MatchedActualActions,
            report.Metrics.JudgeableActualActions);
        return report;
    }

    public async Task<MetaShadowComparisonReport> CompareAsync(
        DateTimeOffset from,
        DateTimeOffset asOf,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await BuildReportAsync(
            db,
            from,
            asOf,
            TimeSpan.FromHours(Math.Max(1, comparisonOptions.Value.MatchWindowHours)),
            ct);
    }

    private static async Task<int> InsertFactsAsync(
        RdDbContext db,
        string adAccountId,
        IReadOnlyList<MetaActivityDto> activities,
        DateTimeOffset recordedAt,
        CancellationToken ct)
    {
        var candidates = activities
            .Select(activity => new { Activity = activity, Fingerprint = Fingerprint(adAccountId, activity) })
            .GroupBy(candidate => candidate.Fingerprint, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        var existing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fingerprints in candidates.Select(candidate => candidate.Fingerprint).Chunk(500))
        {
            var found = await db.MetaActivityFacts.AsNoTracking()
                .Where(fact => fingerprints.Contains(fact.SourceFingerprint))
                .Select(fact => fact.SourceFingerprint)
                .ToListAsync(ct);
            existing.UnionWith(found);
        }

        foreach (var candidate in candidates.Where(candidate => !existing.Contains(candidate.Fingerprint)))
        {
            var activity = candidate.Activity;
            db.MetaActivityFacts.Add(new MetaActivityFact
            {
                Id = Guid.NewGuid(),
                SourceFingerprint = candidate.Fingerprint,
                AdAccountId = adAccountId,
                EventTime = activity.EventTime,
                EventType = activity.EventType,
                ObjectId = activity.ObjectId,
                ObjectName = activity.ObjectName,
                ObjectType = activity.ObjectType,
                ActorId = activity.ActorId,
                ActorName = activity.ActorName,
                ApplicationId = activity.ApplicationId,
                ApplicationName = activity.ApplicationName,
                Tool = activity.Tool,
                TranslatedEventType = activity.TranslatedEventType,
                OldStatus = activity.OldStatus,
                NewStatus = activity.NewStatus,
                ExtraDataJson = activity.ExtraDataJson,
                RecordedAt = recordedAt,
            });
        }

        var inserted = db.ChangeTracker.Entries<MetaActivityFact>()
            .Count(entry => entry.State == EntityState.Added);
        if (inserted > 0) await db.SaveChangesAsync(ct);
        return inserted;
    }

    private static async Task<MetaShadowComparisonReport> BuildReportAsync(
        RdDbContext db,
        DateTimeOffset from,
        DateTimeOffset asOf,
        TimeSpan matchWindow,
        CancellationToken ct)
    {
        var predictions = await db.MetaShadowPredictions.AsNoTracking()
            .Where(prediction => prediction.StartedAt >= from && prediction.StartedAt <= asOf)
            .Select(prediction => new MetaShadowPredictionObservation(
                prediction.Id,
                prediction.ClientId,
                prediction.CampaignId,
                prediction.ProposedAction,
                prediction.DesiredStatus,
                prediction.TargetState,
                prediction.StartedAt,
                prediction.EndedAt))
            .ToListAsync(ct);
        var campaignToClient = (await db.IdentityLinks.AsNoTracking()
                .Where(link => link.System == ExternalSystem.Meta
                               && link.Kind == LinkKind.Campaign
                               && link.InvalidatedAt == null)
                .Select(link => new { link.ExternalId, link.ClientId })
                .ToListAsync(ct))
            .ToDictionary(link => link.ExternalId, link => link.ClientId, StringComparer.Ordinal);
        var facts = await db.MetaActivityFacts.AsNoTracking()
            .Where(fact => fact.EventTime >= from && fact.EventTime <= asOf)
            .Select(fact => new
            {
                fact.Id,
                fact.ObjectId,
                fact.NewStatus,
                fact.EventTime,
            })
            .ToListAsync(ct);
        var activities = facts.Select(fact => new MetaActivityObservation(
            fact.Id,
            campaignToClient.GetValueOrDefault(fact.ObjectId),
            fact.ObjectId,
            fact.NewStatus,
            fact.EventTime));

        return MetaShadowComparison.Compare(predictions, activities, from, asOf, matchWindow);
    }

    private static string Fingerprint(string adAccountId, MetaActivityDto activity)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            adAccountId,
            eventTime = activity.EventTime.ToUniversalTime(),
            activity.EventType,
            activity.ObjectId,
            activity.ActorId,
            activity.ApplicationId,
            activity.Tool,
            activity.OldStatus,
            activity.NewStatus,
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string NormalizeActId(string adAccountId) =>
        adAccountId.StartsWith("act_", StringComparison.Ordinal) ? adAccountId : $"act_{adAccountId}";
}
