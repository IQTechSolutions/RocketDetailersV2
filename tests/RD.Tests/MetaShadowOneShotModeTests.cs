using FluentAssertions;
using RD.Domain;
using RD.Domain.Policy;
using RD.Web.Services;

namespace RD.Tests;

public sealed class MetaShadowOneShotModeTests
{
    [Fact]
    public void Switch_is_removed_before_web_host_configuration_parses_arguments()
    {
        var args = new[] { "--environment", "Production", "--META-SHADOW-COMPARE-ONCE" };

        MetaShadowOneShotMode.IsRequested(args).Should().BeTrue();
        MetaShadowOneShotMode.HostArguments(args)
            .Should().Equal("--environment", "Production");
    }

    [Fact]
    public void Summary_contains_only_aggregate_comparison_results()
    {
        var predictionId = Guid.NewGuid();
        var activityId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        const string campaignId = "campaign-sensitive-id";
        var now = DateTimeOffset.Parse("2026-08-26T12:00:00Z");
        var report = new MetaShadowComparisonReport(
            now.AddDays(-1),
            now,
            TimeSpan.FromHours(24),
            [new MetaShadowComparisonRow(
                predictionId,
                activityId,
                clientId,
                campaignId,
                ProposedActionType.Pause,
                MetaShadowComparison.PausedStatus,
                MetaShadowComparison.PausedStatus,
                now.AddHours(-1),
                now,
                MetaShadowClassification.Matched,
                IsMature: false,
                IsScoreEligible: true,
                TimingLag: TimeSpan.FromHours(1))],
            new MetaShadowMetrics(1, 1, 1m, 1, 1, 1m, TimeSpan.FromHours(1)));

        var json = MetaShadowOneShotMode.SerializeSummary(report);

        json.Should().Contain("\"Matched\": 1");
        json.Should().Contain("\"Rows\": 1");
        json.Should().NotContain(predictionId.ToString());
        json.Should().NotContain(activityId.ToString());
        json.Should().NotContain(clientId.ToString());
        json.Should().NotContain(campaignId);
    }
}
