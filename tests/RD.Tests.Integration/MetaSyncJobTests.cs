using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RD.Domain;
using RD.Infrastructure.Gateways;
using RD.Infrastructure.Sync;
using RD.Tests.Integration.TestInfra;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace RD.Tests.Integration;

public sealed class MetaSyncJobTests : IDisposable
{
    private readonly SyncTestDb _db = new();
    private readonly WireMockServer _server = WireMockServer.Start();
    // Fixed clock: today = 2026-07-24, so the insight sweep covers 07-23 + 07-24.
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        _server.Stop();
        _server.Dispose();
        _db.Dispose();
    }

    private MetaSyncJob CreateJob()
    {
        var options = Options.Create(new MetaOptions
        {
            AccessToken = "meta_test_token",
            AdAccountId = "act_1234",
            BaseUrl = _server.Urls[0],
            AccountCurrency = "USD",
        });
        var gateway = new MetaAdsGateway(
            new HttpClient(), options, Options.Create(new SafetyOptions()),
            new RetryHelper { BaseDelay = TimeSpan.FromMilliseconds(1) });
        return new MetaSyncJob(_db.Factory, gateway, options, _clock, NullLogger<MetaSyncJob>.Instance);
    }

    [Fact]
    public async Task FullSweep_RunTwice_UpsertsCampaigns_OneAdSpendEntryPerCampaignDate()
    {
        var clientId = _db.SeedClientWithLink(ExternalSystem.Meta, LinkKind.Campaign, "camp_1");

        StubCampaigns();
        StubInsights();

        var job = CreateJob();
        await job.RunAsync(CancellationToken.None);
        await job.RunAsync(CancellationToken.None); // idempotency proof

        await using var db = _db.CreateContext();

        // --- Campaign projections upserted (RemoteVersion = updated_time).
        var campaigns = db.MetaCampaigns.ToList();
        campaigns.Should().HaveCount(2);
        var mapped = campaigns.Single(c => c.CampaignId == "camp_1");
        mapped.ClientId.Should().Be(clientId);
        mapped.Name.Should().Be("Detail Launch — Brandon");
        mapped.DailyBudget.Should().Be(25.00m); // "2500" minor units → 25.00
        mapped.EffectiveStatus.Should().Be("ACTIVE");
        mapped.RemoteVersion.Should().Be("2026-07-24T08:00:00+0000");
        mapped.AdAccountId.Should().Be("act_1234");
        campaigns.Single(c => c.CampaignId == "camp_2").ClientId.Should().BeNull();

        // --- Insight projections: one row per campaign-date, upserted.
        var insights = db.MetaInsightsDaily.ToList();
        insights.Should().HaveCount(3);
        var yesterdayRow = insights.Single(i => i.CampaignId == "camp_1" && i.Date == new DateOnly(2026, 7, 23));
        yesterdayRow.Spend.Should().Be(12.34m); // spend is a major-unit decimal string
        yesterdayRow.Clicks.Should().Be(7);
        yesterdayRow.Leads.Should().Be(2);      // only "lead" actions count, purchase ignored
        yesterdayRow.ClientId.Should().Be(clientId);
        insights.Single(i => i.CampaignId == "camp_1" && i.Date == new DateOnly(2026, 7, 24))
            .Spend.Should().Be(3.21m);

        // --- Ledger: exactly one AdSpend entry per MAPPED campaign-date across two runs.
        var ledger = db.LedgerEntries.ToList();
        ledger.Should().HaveCount(2); // camp_2 is unmapped → no ledger row
        ledger.Should().OnlyContain(l =>
            l.Type == LedgerEntryType.AdSpend
            && l.SourceSystem == ExternalSystem.Meta
            && l.ClientId == clientId
            && l.CurrencyCode == "USD"
            && l.SignedAmount < 0); // money out → negative
        ledger.Single(l => l.SourceObjectId == "camp_1:2026-07-23").SignedAmount.Should().Be(-12.34m);
        ledger.Single(l => l.SourceObjectId == "camp_1:2026-07-24").SignedAmount.Should().Be(-3.21m);

        // --- SyncRuns green.
        var runs = db.SyncRuns.ToList();
        runs.Should().HaveCount(2);
        runs.Should().OnlyContain(r =>
            r.System == ExternalSystem.Meta
            && r.Status == SyncRunStatus.Completed
            && r.CompletedAt != null
            && r.ItemsSeen == 5); // 2 campaigns + 3 insight rows

        // Token travels ONLY in the Authorization header, never the query string.
        _server.LogEntries.Should().OnlyContain(e =>
            !e.Url().Contains("meta_test_token")
            && e.Header("Authorization") == "Bearer meta_test_token");
    }

    private void StubCampaigns()
    {
        _server.Given(Request.Create().WithPath("/act_1234/campaigns").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""
                {
                  "data": [
                    {
                      "id": "camp_1", "name": "Detail Launch — Brandon", "status": "ACTIVE",
                      "effective_status": "ACTIVE", "daily_budget": "2500",
                      "updated_time": "2026-07-24T08:00:00+0000"
                    },
                    {
                      "id": "camp_2", "name": "Unmapped Campaign", "status": "PAUSED",
                      "effective_status": "PAUSED",
                      "updated_time": "2026-07-20T10:00:00+0000"
                    }
                  ],
                  "paging": { "cursors": { "before": "b0", "after": "a0" } }
                }
                """));
    }

    private void StubInsights()
    {
        _server.Given(Request.Create().WithPath("/act_1234/insights").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""
                {
                  "data": [
                    {
                      "campaign_id": "camp_1", "spend": "12.34", "clicks": "7",
                      "actions": [
                        { "action_type": "lead", "value": "2" },
                        { "action_type": "purchase", "value": "9" }
                      ],
                      "date_start": "2026-07-23", "date_stop": "2026-07-23"
                    },
                    {
                      "campaign_id": "camp_1", "spend": "3.21", "clicks": "1",
                      "date_start": "2026-07-24", "date_stop": "2026-07-24"
                    },
                    {
                      "campaign_id": "camp_2", "spend": "5.00", "clicks": "2",
                      "date_start": "2026-07-23", "date_stop": "2026-07-23"
                    }
                  ],
                  "paging": { "cursors": { "before": "b0", "after": "a0" } }
                }
                """));
    }
}
