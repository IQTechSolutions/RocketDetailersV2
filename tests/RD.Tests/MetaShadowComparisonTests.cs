using FluentAssertions;
using RD.Domain;
using RD.Domain.Policy;

namespace RD.Tests;

public sealed class MetaShadowComparisonTests
{
    private static readonly DateTimeOffset From = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset AsOf = From.AddDays(3);
    private static readonly TimeSpan MatchWindow = TimeSpan.FromHours(24);

    [Fact]
    public void Compare_Classifies_every_shadow_outcome_and_scores_only_judgeable_rows()
    {
        var match = Prediction("camp_match", MetaShadowTargetState.Executable, From.AddHours(1));
        var predictedOnly = Prediction("camp_predicted", MetaShadowTargetState.Executable, From.AddHours(2));
        var opposite = Prediction("camp_opposite", MetaShadowTargetState.Executable, From.AddHours(3));
        var alreadySatisfied = Prediction("camp_satisfied", MetaShadowTargetState.AlreadySatisfied, From.AddHours(4));
        var noTarget = Prediction(null, MetaShadowTargetState.NoActiveTarget, From.AddHours(5));
        var unknownTarget = Prediction("camp_archived", MetaShadowTargetState.Unjudgeable, From.AddHours(6));
        var pending = Prediction("camp_pending", MetaShadowTargetState.Executable, AsOf.AddHours(-1));

        var matchedActivity = Activity("camp_match", "PAUSED", match.StartedAt.AddHours(2));
        var oppositeActivity = Activity("camp_opposite", "ACTIVE", opposite.StartedAt.AddHours(1));
        var actualOnlyActivity = Activity("camp_actual", "PAUSED", From.AddHours(8));
        var unmappedActivity = Activity("camp_unmapped", "PAUSED", From.AddHours(9), mapped: false);
        var unknownActivity = Activity("camp_unknown", null, From.AddHours(10));

        var report = MetaShadowComparison.Compare(
            [match, predictedOnly, opposite, alreadySatisfied, noTarget, unknownTarget, pending],
            [matchedActivity, oppositeActivity, actualOnlyActivity, unmappedActivity, unknownActivity],
            From,
            AsOf,
            MatchWindow);

        report.Rows.Should().ContainSingle(row => row.Classification == MetaShadowClassification.Matched);
        report.Rows.Should().Contain(row =>
            row.PredictionId == predictedOnly.Id
            && row.Classification == MetaShadowClassification.PredictedOnly
            && row.IsMature
            && row.IsScoreEligible);
        report.Rows.Should().Contain(row =>
            row.PredictionId == pending.Id
            && row.Classification == MetaShadowClassification.PredictedOnly
            && !row.IsMature
            && !row.IsScoreEligible);
        report.Rows.Should().ContainSingle(row => row.Classification == MetaShadowClassification.OppositeAction);
        report.Rows.Should().ContainSingle(row => row.Classification == MetaShadowClassification.AlreadySatisfied);
        report.Rows.Should().ContainSingle(row => row.Classification == MetaShadowClassification.NoActiveTarget);
        report.Rows.Should().ContainSingle(row => row.Classification == MetaShadowClassification.ActualOnly);
        report.Rows.Count(row => row.Classification == MetaShadowClassification.Unjudgeable).Should().Be(3);

        report.Metrics.MatchedPredictions.Should().Be(1);
        report.Metrics.ScoredPredictions.Should().Be(3);
        report.Metrics.PrecisionRate.Should().Be(1m / 3m);
        report.Metrics.MatchedActualActions.Should().Be(1);
        report.Metrics.JudgeableActualActions.Should().Be(3);
        report.Metrics.CoverageRate.Should().Be(1m / 3m);
        report.Metrics.AverageTimingLag.Should().Be(TimeSpan.FromHours(2));
    }

    [Fact]
    public void Compare_Does_not_match_an_action_that_happened_before_the_prediction()
    {
        var prediction = Prediction("camp_1", MetaShadowTargetState.Executable, From.AddHours(4));
        var earlierActivity = Activity("camp_1", "PAUSED", From.AddHours(3));

        var report = MetaShadowComparison.Compare(
            [prediction],
            [earlierActivity],
            From,
            AsOf,
            MatchWindow);

        report.Rows.Should().ContainSingle(row =>
            row.PredictionId == prediction.Id
            && row.Classification == MetaShadowClassification.PredictedOnly);
        report.Rows.Should().ContainSingle(row =>
            row.ActivityId == earlierActivity.Id
            && row.Classification == MetaShadowClassification.ActualOnly);
    }

    [Fact]
    public void Compare_Does_not_score_a_prediction_withdrawn_before_the_response_window_ended()
    {
        var prediction = Prediction("camp_1", MetaShadowTargetState.Executable, From.AddHours(4)) with
        {
            EndedAt = From.AddHours(6),
        };

        var report = MetaShadowComparison.Compare(
            [prediction],
            [],
            From,
            AsOf,
            MatchWindow);

        report.Rows.Should().ContainSingle(row =>
            row.PredictionId == prediction.Id
            && row.Classification == MetaShadowClassification.Unjudgeable
            && !row.IsScoreEligible);
        report.Metrics.ScoredPredictions.Should().Be(0);
        report.Metrics.PrecisionRate.Should().BeNull();
    }

    private static MetaShadowPredictionObservation Prediction(
        string? campaignId,
        MetaShadowTargetState targetState,
        DateTimeOffset startedAt) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            campaignId,
            ProposedActionType.Pause,
            MetaShadowComparison.PausedStatus,
            targetState,
            startedAt,
            EndedAt: null);

    private static MetaActivityObservation Activity(
        string campaignId,
        string? newStatus,
        DateTimeOffset eventTime,
        bool mapped = true) =>
        new(
            Guid.NewGuid(),
            mapped ? Guid.NewGuid() : null,
            campaignId,
            newStatus,
            eventTime);
}
