using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RD.Domain;
using RD.Infrastructure.Enforcement;
using RD.Infrastructure.Gateways;
using RD.Tests.Integration.TestInfra;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace RD.Tests.Integration;

/// <summary>
/// The retry ladder: a transient gateway failure releases the action back to
/// Approved with an incremented attempt count and a scheduled backoff; a
/// failure that persists through MaxAttempts dead-letters the action for a
/// human instead of hammering Meta forever.
/// </summary>
public sealed class OutboxDispatcherRetryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 07, 24, 12, 0, 0, TimeSpan.Zero);
    private readonly SyncTestDb _db = new();
    private readonly WireMockServer _meta = WireMockServer.Start();
    private readonly TestClock _clock = new(Now);

    private OutboxDispatcher BuildDispatcher(SafetyOptions safety)
    {
        var metaOptions = Options.Create(new MetaOptions { AccessToken = "t", AdAccountId = "act_1", BaseUrl = _meta.Urls[0] });
        var safetyOpt = Options.Create(safety);
        var retry = new RetryHelper { BaseDelay = TimeSpan.FromMilliseconds(1) };
        var metaGw = new MetaAdsGateway(new HttpClient(), metaOptions, safetyOpt, retry);
        var ghlGw = new GhlGateway(new HttpClient(),
            Options.Create(new GhlOptions { BaseUrl = _meta.Urls[0], Locations = [] }), safetyOpt, retry);
        var enf = Options.Create(new EnforcementOptions());
        var builder = new ClientStateBuilder(_clock, enf);
        return new OutboxDispatcher(_db.Factory, builder, metaGw, ghlGw, _clock, enf, NullLogger<OutboxDispatcher>.Instance);
    }

    /// <summary>Every campaign read fails with 500 — the pre-write read-back inside ExecutePauseAsync throws.</summary>
    private void StubCampaignGetAlwaysFails() =>
        _meta.Given(Request.Create().WithPath($"/{EnforcementSeed.CampaignId}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(500)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"error":{"message":"transient"}}"""));

    // ── One transient failure: released for retry with attempt count + backoff, never dead-lettered early ──

    [Fact]
    public async Task Transient_gateway_failure_releases_action_with_attempt_and_backoff()
    {
        var clientId = EnforcementSeed.SeedMappedClient(_db, subscriptionStatus: "canceled", campaignEffectiveStatus: "ACTIVE", Now);
        var actionId = EnforcementSeed.SeedApprovedPause(_db, clientId, EnforcementSeed.CampaignId, expectedEpoch: 0, Now);
        StubCampaignGetAlwaysFails();

        await BuildDispatcher(new SafetyOptions { AllowProductionMetaWrites = true }).RunAsync(default);

        await using var ctx = _db.CreateContext();
        var action = await ctx.OutboxActions.FindAsync(actionId);
        action!.Status.Should().Be(OutboxStatus.Approved);          // released, not dead-lettered
        action.Attempts.Should().Be(1);                             // the claim charged one attempt
        action.LeaseUntil.Should().BeNull();                        // lease released for the next pass
        action.NextAttemptAt.Should().Be(Now.AddSeconds(15));       // backoff ladder rung 1: 15s
        action.LastError.Should().NotBeNullOrEmpty();
        // The write itself never happened and no provenance was recorded.
        _meta.LogEntries.Where(e => e.RequestMessage.Method == "POST").Should().BeEmpty();
        (await ctx.PauseOperations.AnyAsync()).Should().BeFalse();
    }

    // ── The ladder ends: MaxAttempts transient failures ⇒ DeadLettered, attempts fully charged ──

    [Fact]
    public async Task Persistent_transient_failure_dead_letters_after_max_attempts()
    {
        var clientId = EnforcementSeed.SeedMappedClient(_db, subscriptionStatus: "canceled", campaignEffectiveStatus: "ACTIVE", Now);
        var actionId = EnforcementSeed.SeedApprovedPause(_db, clientId, EnforcementSeed.CampaignId, expectedEpoch: 0, Now);
        StubCampaignGetAlwaysFails();

        var maxAttempts = new EnforcementOptions().MaxAttempts;
        var dispatcher = BuildDispatcher(new SafetyOptions { AllowProductionMetaWrites = true });
        for (var pass = 0; pass < maxAttempts; pass++)
        {
            // Each transient failure schedules a backoff the claim query now honors,
            // so advance the clock past it before the next pass can re-claim.
            if (pass > 0)
            {
                await using var peek = _db.CreateContext();
                var next = (await peek.OutboxActions.FindAsync(actionId))!.NextAttemptAt;
                if (next is not null) _clock.UtcNow = next.Value.AddSeconds(1);
            }
            await dispatcher.RunAsync(default);
        }

        await using var ctx = _db.CreateContext();
        var action = await ctx.OutboxActions.FindAsync(actionId);
        action!.Status.Should().Be(OutboxStatus.DeadLettered);
        action.Attempts.Should().Be(maxAttempts);                   // every rung of the ladder was climbed
        action.LastError.Should().NotBeNullOrEmpty();
        _meta.LogEntries.Where(e => e.RequestMessage.Method == "POST").Should().BeEmpty();
        (await ctx.PauseOperations.AnyAsync()).Should().BeFalse();

        // Dead-lettered is terminal: one more pass claims nothing and changes nothing.
        await dispatcher.RunAsync(default);
        await using var after = _db.CreateContext();
        (await after.OutboxActions.FindAsync(actionId))!.Attempts.Should().Be(maxAttempts);
    }

    // ── Backoff is actually enforced: a released action is not re-claimable until NextAttemptAt is due ──

    [Fact]
    public async Task Scheduled_backoff_is_enforced_action_not_reclaimed_until_due()
    {
        var clientId = EnforcementSeed.SeedMappedClient(_db, subscriptionStatus: "canceled", campaignEffectiveStatus: "ACTIVE", Now);
        var actionId = EnforcementSeed.SeedApprovedPause(_db, clientId, EnforcementSeed.CampaignId, expectedEpoch: 0, Now);
        StubCampaignGetAlwaysFails();
        var dispatcher = BuildDispatcher(new SafetyOptions { AllowProductionMetaWrites = true });

        // Pass 1: transient failure schedules a 15s backoff.
        await dispatcher.RunAsync(default);
        DateTimeOffset scheduled;
        await using (var ctx = _db.CreateContext())
        {
            var a = await ctx.OutboxActions.FindAsync(actionId);
            a!.Attempts.Should().Be(1);
            scheduled = a.NextAttemptAt!.Value;
        }

        // Pass 2 at the SAME clock: still inside the backoff window → NOT re-claimed.
        await dispatcher.RunAsync(default);
        await using (var ctx = _db.CreateContext())
            (await ctx.OutboxActions.FindAsync(actionId))!.Attempts.Should().Be(1); // unchanged — backoff respected

        // Advance past the scheduled time: the action is claimable again and charges a second attempt.
        _clock.UtcNow = scheduled.AddSeconds(1);
        await dispatcher.RunAsync(default);
        await using (var ctx = _db.CreateContext())
            (await ctx.OutboxActions.FindAsync(actionId))!.Attempts.Should().Be(2);
    }

    public void Dispose()
    {
        _meta.Stop();
        _db.Dispose();
    }
}
