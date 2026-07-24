using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RD.Domain;
using RD.Infrastructure.Gateways;
using RD.Infrastructure.Sync;
using RD.Tests.Integration.TestInfra;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace RD.Tests.Integration;

/// <summary>
/// Complete-sweep semantics: a partially paginated sync must NEVER look green.
/// Page 2 of the subscription sweep fails (after retries) → the SyncRun is
/// Failed with the error recorded, and the job swallows (the schedule is the
/// retry authority).
/// </summary>
public sealed class SyncRunPartialFailureTests : IDisposable
{
    private readonly SyncTestDb _db = new();
    private readonly WireMockServer _server = WireMockServer.Start();
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        _server.Stop();
        _server.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task PageTwoKeepsFailing_SyncRunIsFailed_NeverCompleted_AndJobSwallows()
    {
        // Page 1 succeeds and promises more…
        _server.Given(Request.Create().WithPath("/v1/subscriptions")
                .WithParam("starting_after", MatchBehaviour.RejectOnMatch).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""
                {
                  "object": "list",
                  "has_more": true,
                  "data": [ { "id": "sub_1", "customer": "cus_1", "status": "active", "currency": "usd" } ]
                }
                """));

        // …page 2 is a hard 500, every attempt.
        _server.Given(Request.Create().WithPath("/v1/subscriptions")
                .WithParam("starting_after", "sub_1").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(500).WithBody("stripe exploded"));

        var options = Options.Create(new StripeOptions { ApiKey = "rk_test_dummy", BaseUrl = _server.Urls[0] });
        var gateway = new StripeGateway(
            new HttpClient(), options, new RetryHelper { BaseDelay = TimeSpan.FromMilliseconds(1) });
        var job = new StripeSyncJob(_db.Factory, gateway, options, _clock, NullLogger<StripeSyncJob>.Instance);

        // Must not throw — failures are recorded, not propagated.
        await job.RunAsync(CancellationToken.None);

        await using var db = _db.CreateContext();
        var runs = db.SyncRuns.ToList();
        runs.Should().HaveCount(1);
        runs[0].System.Should().Be(ExternalSystem.Stripe);
        runs[0].Status.Should().Be(SyncRunStatus.Failed);
        runs[0].Status.Should().NotBe(SyncRunStatus.Completed);
        runs[0].CompletedAt.Should().BeNull("a partial sweep must never look green");
        runs[0].Error.Should().Contain("500");

        // RetryHelper proof: page 2 was attempted exactly 3 times.
        _server.LogEntries
            .Count(e => e.Url().Contains("starting_after=sub_1"))
            .Should().Be(3);

        // The sweep never reached the invoice listings.
        _server.LogEntries.Should().NotContain(e => e.Path().StartsWith("/v1/invoices"));

        // And no partial money was ingested.
        db.LedgerEntries.Should().BeEmpty();
    }
}
