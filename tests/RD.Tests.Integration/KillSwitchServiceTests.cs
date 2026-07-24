using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RD.Domain;
using RD.Domain.Entities;
using RD.Infrastructure.Enforcement;
using RD.Infrastructure.Gateways;
using RD.Tests.Integration.TestInfra;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace RD.Tests.Integration;

/// <summary>
/// The global stop. Engage must bump the epoch (so every staged action
/// supersedes at its pre-write epoch check) AND revert the whole fleet to
/// Shadow; Release lets the dispatcher run again under the NEW epoch, but the
/// ladder is never restored automatically.
/// </summary>
public sealed class KillSwitchServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 07, 24, 12, 0, 0, TimeSpan.Zero);
    private readonly SyncTestDb _db = new();
    private readonly WireMockServer _meta = WireMockServer.Start();
    private readonly TestClock _clock = new(Now);

    private KillSwitchService CreateService() => new(_db.Factory, _clock);

    // ── Engage: epoch bump + fleet reverted to Shadow ──

    [Fact]
    public async Task Engage_bumps_epoch_and_reverts_the_whole_fleet_to_shadow()
    {
        Guid assistId, autoId, shadowId;
        await using (var seed = _db.CreateContext())
        {
            Client NewClient(string name, EnforcementMode mode) => new()
            {
                Id = Guid.NewGuid(), BusinessName = name, ContractType = ContractType.Paid,
                AccountType = AccountType.Master, EnforcementMode = mode, CreatedAt = Now,
            };
            var assist = NewClient("Assist Co", EnforcementMode.Assist);
            var auto = NewClient("Auto Co", EnforcementMode.Auto);
            var shadow = NewClient("Shadow Co", EnforcementMode.Shadow);
            seed.Clients.AddRange(assist, auto, shadow);
            await seed.SaveChangesAsync();
            (assistId, autoId, shadowId) = (assist.Id, auto.Id, shadow.Id);
        }

        var epoch = await CreateService().EngageAsync("ivan", "kill-switch drill");

        epoch.Should().Be(1); // first row is created at epoch 0, then bumped by the engage

        await using var ctx = _db.CreateContext();
        var ks = await ctx.KillSwitch.SingleAsync(k => k.Id == 1);
        ks.Enabled.Should().BeTrue();
        ks.Epoch.Should().Be(1);
        ks.Actor.Should().Be("ivan");
        ks.Reason.Should().Be("kill-switch drill");
        ks.ChangedAt.Should().Be(Now);

        // The entire fleet is Shadow — Assist and Auto were both demoted.
        (await ctx.Clients.FindAsync(assistId))!.EnforcementMode.Should().Be(EnforcementMode.Shadow);
        (await ctx.Clients.FindAsync(autoId))!.EnforcementMode.Should().Be(EnforcementMode.Shadow);
        (await ctx.Clients.FindAsync(shadowId))!.EnforcementMode.Should().Be(EnforcementMode.Shadow);

        (await CreateService().IsEngagedAsync()).Should().BeTrue();
    }

    // ── Release: dispatch runs again under the new epoch, but the ladder stays Shadow ──

    [Fact]
    public async Task Release_restores_dispatch_under_the_new_epoch_but_not_the_ladder()
    {
        var service = CreateService();
        await service.EngageAsync("ivan", "drill");                       // epoch 0 → 1
        var epochAfterRelease = await service.ReleaseAsync("ivan", "drill over"); // epoch 1 → 2

        epochAfterRelease.Should().Be(2);
        (await service.IsEngagedAsync()).Should().BeFalse();

        // A client mapped after the incident, with a pause staged under the CURRENT
        // epoch, dispatches normally — release genuinely restores eligibility.
        var clientId = EnforcementSeed.SeedMappedClient(_db, subscriptionStatus: "canceled", campaignEffectiveStatus: "ACTIVE", Now);
        var actionId = EnforcementSeed.SeedApprovedPause(_db, clientId, EnforcementSeed.CampaignId, expectedEpoch: epochAfterRelease, Now);
        StubPauseScenario();

        await BuildDispatcher(new SafetyOptions { AllowProductionMetaWrites = true }).RunAsync(default);

        await using var ctx = _db.CreateContext();
        (await ctx.OutboxActions.FindAsync(actionId))!.Status.Should().Be(OutboxStatus.Executed);
        (await ctx.PauseOperations.SingleAsync(p => p.ClientId == clientId)).State.Should().Be(PauseOperationState.Paused);

        // Releasing did NOT re-promote anyone: the demoted fleet stays Shadow.
        var ks = await ctx.KillSwitch.SingleAsync(k => k.Id == 1);
        ks.Enabled.Should().BeFalse();
        ks.Reason.Should().Be("drill over");
    }

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

    /// <summary>Campaign reads ACTIVE until the pause POST lands, then PAUSED (read-back convergence).</summary>
    private void StubPauseScenario()
    {
        _meta.Given(Request.Create().WithPath($"/{EnforcementSeed.CampaignId}").UsingGet())
            .InScenario("pause")
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody($$"""{"id":"{{EnforcementSeed.CampaignId}}","status":"ACTIVE","effective_status":"ACTIVE"}"""));
        _meta.Given(Request.Create().WithPath($"/{EnforcementSeed.CampaignId}").UsingPost())
            .InScenario("pause")
            .WillSetStateTo("paused")
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("""{"success":true}"""));
        _meta.Given(Request.Create().WithPath($"/{EnforcementSeed.CampaignId}").UsingGet())
            .InScenario("pause")
            .WhenStateIs("paused")
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody($$"""{"id":"{{EnforcementSeed.CampaignId}}","status":"PAUSED","effective_status":"PAUSED"}"""));
    }

    public void Dispose()
    {
        _meta.Stop();
        _db.Dispose();
    }
}
