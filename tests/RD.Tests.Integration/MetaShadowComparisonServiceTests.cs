using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RD.Domain;
using RD.Domain.Entities;
using RD.Infrastructure.Gateways;
using RD.Infrastructure.Sync;
using RD.Tests.Integration.TestInfra;

namespace RD.Tests.Integration;

public sealed class MetaShadowComparisonServiceTests : IDisposable
{
    private static readonly DateTimeOffset PredictionAt = new(2026, 8, 24, 10, 0, 0, TimeSpan.Zero);
    private readonly SyncTestDb _db = new();

    [Fact]
    public async Task Sync_and_compare_is_GET_only_idempotent_and_matches_by_campaign_status_and_time()
    {
        const string campaignId = "camp_shadow_1";
        var clientId = _db.SeedClientWithLink(ExternalSystem.Meta, LinkKind.Campaign, campaignId);
        await using (var seed = _db.CreateContext())
        {
            var decision = new Decision
            {
                Id = Guid.NewGuid(),
                ClientId = clientId,
                EvaluatedAt = PredictionAt,
                PolicyVersion = "test",
                StateSnapshotJson = "{}",
                ProposedAction = ProposedActionType.Pause,
                Mode = EnforcementMode.Shadow,
                TargetCampaignIdsJson = $"[\"{campaignId}\"]",
                Reason = "Test prediction",
            };
            seed.Decisions.Add(decision);
            seed.MetaShadowPredictions.Add(new MetaShadowPrediction
            {
                Id = Guid.NewGuid(),
                ClientId = clientId,
                DecisionId = decision.Id,
                CampaignId = campaignId,
                ProposedAction = ProposedActionType.Pause,
                DesiredStatus = "PAUSED",
                TargetState = MetaShadowTargetState.Executable,
                StartedAt = PredictionAt,
            });
            await seed.SaveChangesAsync();
        }

        var handler = new GetOnlyHandler("""
            {
              "data": [
                {
                  "event_time": "2026-08-24T12:00:00+0000",
                  "event_type": "update_campaign_run_status",
                  "object_id": "camp_shadow_1",
                  "actor_id": "operator_1",
                  "extra_data": "{\"old_value\":\"Active\",\"new_value\":\"Inactive\"}"
                }
              ]
            }
            """);
        var meta = Options.Create(new MetaOptions
        {
            AccessToken = "meta_test_token",
            AdAccountId = "act_1234",
            BaseUrl = "https://graph.facebook.test/v25.0",
        });
        var reader = new MetaActivityReader(
            new HttpClient(handler),
            meta,
            new RetryHelper { BaseDelay = TimeSpan.Zero });
        var service = new MetaShadowComparisonService(
            _db.Factory,
            reader,
            meta,
            Options.Create(new MetaShadowComparisonOptions
            {
                ActivityLookbackDays = 30,
                ActivityOverlapHours = 24,
                MatchWindowHours = 24,
            }),
            new TestClock(PredictionAt.AddHours(3)),
            NullLogger<MetaShadowComparisonService>.Instance);

        var first = await service.SyncAndCompareAsync(CancellationToken.None);
        var second = await service.SyncAndCompareAsync(CancellationToken.None);

        first.Metrics.PrecisionRate.Should().Be(1m);
        first.Metrics.CoverageRate.Should().Be(1m);
        first.Metrics.AverageTimingLag.Should().Be(TimeSpan.FromHours(2));
        second.Metrics.Should().Be(first.Metrics);
        handler.Methods.Should().HaveCount(2).And.OnlyContain(method => method == HttpMethod.Get);

        await using var assert = _db.CreateContext();
        var fact = await assert.MetaActivityFacts.SingleAsync();
        fact.ObjectId.Should().Be(campaignId);
        fact.NewStatus.Should().Be("PAUSED");
        assert.OutboxActions.Should().BeEmpty();

        fact.ActorName = "tampered";
        var save = () => assert.SaveChangesAsync();
        await save.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*MetaActivityFact is immutable*");
    }

    public void Dispose() => _db.Dispose();

    private sealed class GetOnlyHandler(string responseBody) : HttpMessageHandler
    {
        public List<HttpMethod> Methods { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Methods.Add(request.Method);
            if (request.Method != HttpMethod.Get)
                throw new InvalidOperationException($"Shadow comparison attempted forbidden HTTP method {request.Method}.");
            if (request.Headers.Authorization?.ToString() != "Bearer meta_test_token")
                throw new InvalidOperationException("Meta authorization header was missing.");
            if (request.RequestUri?.Query.Contains("meta_test_token", StringComparison.Ordinal) == true)
                throw new InvalidOperationException("Meta token leaked into the request URL.");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            });
        }
    }
}
