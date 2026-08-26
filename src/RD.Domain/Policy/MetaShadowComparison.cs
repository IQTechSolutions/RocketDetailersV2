namespace RD.Domain.Policy;

/// <summary>A persisted shadow prediction projected into the pure comparison engine.</summary>
public sealed record MetaShadowPredictionObservation(
    Guid Id,
    Guid ClientId,
    string? CampaignId,
    ProposedActionType ProposedAction,
    string DesiredStatus,
    MetaShadowTargetState TargetState,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt);

/// <summary>A Meta campaign-status activity projected into the pure comparison engine.</summary>
public sealed record MetaActivityObservation(
    Guid Id,
    Guid? ClientId,
    string CampaignId,
    string? NewStatus,
    DateTimeOffset EventTime);

public sealed record MetaShadowComparisonRow(
    Guid? PredictionId,
    Guid? ActivityId,
    Guid? ClientId,
    string? CampaignId,
    ProposedActionType? PredictedAction,
    string? DesiredStatus,
    string? ActualStatus,
    DateTimeOffset? PredictedAt,
    DateTimeOffset? ActualAt,
    MetaShadowClassification Classification,
    bool IsMature,
    bool IsScoreEligible,
    TimeSpan? TimingLag);

public sealed record MetaShadowMetrics(
    int MatchedPredictions,
    int ScoredPredictions,
    decimal? PrecisionRate,
    int MatchedActualActions,
    int JudgeableActualActions,
    decimal? CoverageRate,
    TimeSpan? AverageTimingLag);

public sealed record MetaShadowComparisonReport(
    DateTimeOffset From,
    DateTimeOffset AsOf,
    TimeSpan MatchWindow,
    IReadOnlyList<MetaShadowComparisonRow> Rows,
    MetaShadowMetrics Metrics);

/// <summary>
/// Pure matcher for V2 shadow recommendations against Meta's observed audit
/// trail. An activity can satisfy at most one prediction, and only activity
/// after the prediction can count as a match.
/// </summary>
public static class MetaShadowComparison
{
    public const string ActiveStatus = "ACTIVE";
    public const string PausedStatus = "PAUSED";

    public static MetaShadowComparisonReport Compare(
        IEnumerable<MetaShadowPredictionObservation> predictions,
        IEnumerable<MetaActivityObservation> activities,
        DateTimeOffset from,
        DateTimeOffset asOf,
        TimeSpan matchWindow)
    {
        if (asOf < from) throw new ArgumentOutOfRangeException(nameof(asOf), "AsOf must not precede From.");
        if (matchWindow <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(matchWindow));

        var activityRows = activities
            .Where(a => a.EventTime >= from && a.EventTime <= asOf)
            .OrderBy(a => a.EventTime)
            .ThenBy(a => a.Id)
            .ToList();
        var activitiesByCampaign = activityRows
            .GroupBy(a => a.CampaignId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var usedActivityIds = new HashSet<Guid>();
        var rows = new List<MetaShadowComparisonRow>();

        foreach (var prediction in predictions
                     .Where(p => p.StartedAt >= from && p.StartedAt <= asOf)
                     .OrderBy(p => p.StartedAt)
                     .ThenBy(p => p.Id))
        {
            if (prediction.TargetState != MetaShadowTargetState.Executable)
            {
                rows.Add(NonExecutableRow(prediction));
                continue;
            }

            if (string.IsNullOrWhiteSpace(prediction.CampaignId)
                || !IsJudgeableStatus(prediction.DesiredStatus))
            {
                rows.Add(PredictionRow(
                    prediction,
                    activity: null,
                    MetaShadowClassification.Unjudgeable,
                    isMature: false,
                    isScoreEligible: false));
                continue;
            }

            var deadline = prediction.StartedAt + matchWindow;
            var matchCutoff = prediction.EndedAt is { } endedAt && endedAt < deadline
                ? endedAt
                : deadline;
            if (matchCutoff > asOf) matchCutoff = asOf;

            MetaActivityObservation? activity = null;
            if (activitiesByCampaign.TryGetValue(prediction.CampaignId, out var campaignActivities))
            {
                activity = campaignActivities.FirstOrDefault(a =>
                    !usedActivityIds.Contains(a.Id)
                    && a.EventTime >= prediction.StartedAt
                    && a.EventTime <= matchCutoff);
            }

            var mature = asOf >= deadline;
            if (activity is null)
            {
                if (prediction.EndedAt is { } endedBeforeDeadline && endedBeforeDeadline < deadline)
                {
                    rows.Add(PredictionRow(
                        prediction,
                        activity: null,
                        MetaShadowClassification.Unjudgeable,
                        isMature: true,
                        isScoreEligible: false));
                    continue;
                }

                rows.Add(PredictionRow(
                    prediction,
                    activity: null,
                    MetaShadowClassification.PredictedOnly,
                    mature,
                    isScoreEligible: mature));
                continue;
            }

            usedActivityIds.Add(activity.Id);
            if (!IsJudgeableStatus(activity.NewStatus))
            {
                rows.Add(PredictionRow(
                    prediction,
                    activity,
                    MetaShadowClassification.Unjudgeable,
                    mature,
                    isScoreEligible: false));
                continue;
            }

            var matched = string.Equals(
                prediction.DesiredStatus,
                activity.NewStatus,
                StringComparison.Ordinal);
            rows.Add(PredictionRow(
                prediction,
                activity,
                matched ? MetaShadowClassification.Matched : MetaShadowClassification.OppositeAction,
                mature,
                isScoreEligible: true));
        }

        foreach (var activity in activityRows.Where(a => !usedActivityIds.Contains(a.Id)))
        {
            var judgeable = activity.ClientId is not null && IsJudgeableStatus(activity.NewStatus);
            rows.Add(new MetaShadowComparisonRow(
                PredictionId: null,
                ActivityId: activity.Id,
                ClientId: activity.ClientId,
                CampaignId: activity.CampaignId,
                PredictedAction: null,
                DesiredStatus: null,
                ActualStatus: activity.NewStatus,
                PredictedAt: null,
                ActualAt: activity.EventTime,
                Classification: judgeable
                    ? MetaShadowClassification.ActualOnly
                    : MetaShadowClassification.Unjudgeable,
                IsMature: true,
                IsScoreEligible: judgeable,
                TimingLag: null));
        }

        var scoredPredictions = rows.Count(r => r.PredictionId is not null && r.IsScoreEligible);
        var matchedPredictions = rows.Count(r =>
            r.PredictionId is not null && r.Classification == MetaShadowClassification.Matched);
        var judgeableActualActions = rows.Count(r =>
            r.ActivityId is not null
            && r.Classification is MetaShadowClassification.Matched
                or MetaShadowClassification.OppositeAction
                or MetaShadowClassification.ActualOnly);
        var matchedActualActions = rows.Count(r =>
            r.ActivityId is not null && r.Classification == MetaShadowClassification.Matched);
        var matchedLags = rows
            .Where(r => r.Classification == MetaShadowClassification.Matched && r.TimingLag is not null)
            .Select(r => r.TimingLag!.Value)
            .ToList();

        var metrics = new MetaShadowMetrics(
            matchedPredictions,
            scoredPredictions,
            Divide(matchedPredictions, scoredPredictions),
            matchedActualActions,
            judgeableActualActions,
            Divide(matchedActualActions, judgeableActualActions),
            matchedLags.Count == 0
                ? null
                : TimeSpan.FromTicks((long)matchedLags.Average(lag => lag.Ticks)));

        return new MetaShadowComparisonReport(from, asOf, matchWindow, rows, metrics);
    }

    private static MetaShadowComparisonRow NonExecutableRow(MetaShadowPredictionObservation prediction)
    {
        var classification = prediction.TargetState switch
        {
            MetaShadowTargetState.AlreadySatisfied => MetaShadowClassification.AlreadySatisfied,
            MetaShadowTargetState.NoActiveTarget => MetaShadowClassification.NoActiveTarget,
            _ => MetaShadowClassification.Unjudgeable,
        };
        return PredictionRow(
            prediction,
            activity: null,
            classification,
            isMature: true,
            isScoreEligible: false);
    }

    private static MetaShadowComparisonRow PredictionRow(
        MetaShadowPredictionObservation prediction,
        MetaActivityObservation? activity,
        MetaShadowClassification classification,
        bool isMature,
        bool isScoreEligible) =>
        new(
            prediction.Id,
            activity?.Id,
            prediction.ClientId,
            prediction.CampaignId,
            prediction.ProposedAction,
            prediction.DesiredStatus,
            activity?.NewStatus,
            prediction.StartedAt,
            activity?.EventTime,
            classification,
            isMature,
            isScoreEligible,
            activity is null ? null : activity.EventTime - prediction.StartedAt);

    private static bool IsJudgeableStatus(string? status) =>
        status is ActiveStatus or PausedStatus;

    private static decimal? Divide(int numerator, int denominator) =>
        denominator == 0 ? null : (decimal)numerator / denominator;
}
