using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RD.Domain;
using RD.Domain.Entities;
using RD.Infrastructure.Enforcement;
using RD.Infrastructure.Gateways;
using RD.Infrastructure.Reconciliation;
using RD.Tests.Integration.TestInfra;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace RD.Tests.Integration;

/// <summary>
/// The dispatcher is the component that can pause real ads. These tests prove
/// the guardrails: revalidation stops a stale pause, the canary/safety gate
/// refuses production writes, the kill-switch epoch supersedes, and an approved
/// pause only executes when the world still agrees.
/// </summary>
public sealed class OutboxDispatcherTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 07, 24, 12, 0, 0, TimeSpan.Zero);
    private readonly SyncTestDb _db = new();
    private readonly WireMockServer _meta = WireMockServer.Start();
    private readonly TestClock _clock = new(Now);

    private OutboxDispatcher BuildDispatcher(
        SafetyOptions safety,
        Func<Guid, CancellationToken, Task>? beforeClientFence = null)
    {
        var metaOptions = Options.Create(new MetaOptions { AccessToken = "t", AdAccountId = "act_1", BaseUrl = _meta.Urls[0] });
        var safetyOpt = Options.Create(safety);
        var retry = new RetryHelper { BaseDelay = TimeSpan.FromMilliseconds(1) };
        var metaGw = new MetaAdsGateway(new HttpClient(), metaOptions, safetyOpt, retry);
        var ghlGw = new GhlGateway(new HttpClient(),
            Options.Create(new GhlOptions { BaseUrl = _meta.Urls[0], Locations = [] }), safetyOpt, retry);
        var enf = Options.Create(new EnforcementOptions());
        var builder = new ClientStateBuilder(_clock, enf);
        return new OutboxDispatcher(
            _db.Factory,
            builder,
            metaGw,
            ghlGw,
            _clock,
            enf,
            NullLogger<OutboxDispatcher>.Instance,
            beforeClientFence);
    }

    private void StubCampaignGet(string status, string effectiveStatus) =>
        _meta.Given(Request.Create().WithPath($"/{EnforcementSeed.CampaignId}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody($$"""{"id":"{{EnforcementSeed.CampaignId}}","status":"{{status}}","effective_status":"{{effectiveStatus}}"}"""));

    // ── Revalidation: a paid-up client is NEVER paused, even with an approved pause queued ──

    [Fact]
    public async Task Approved_pause_is_superseded_when_client_is_now_paid_up()
    {
        // Sub is ACTIVE + campaign running ⇒ current verdict is "None" (paid up), not Pause.
        var clientId = EnforcementSeed.SeedMappedClient(_db, subscriptionStatus: "active", campaignEffectiveStatus: "ACTIVE", Now);
        var actionId = EnforcementSeed.SeedApprovedPause(_db, clientId, EnforcementSeed.CampaignId, expectedEpoch: 0, Now);

        await BuildDispatcher(new SafetyOptions { AllowProductionMetaWrites = true }).RunAsync(default);

        await using var ctx = _db.CreateContext();
        var action = await ctx.OutboxActions.FindAsync(actionId);
        action!.Status.Should().Be(OutboxStatus.Superseded);
        // No Meta write happened.
        _meta.LogEntries.Where(e => e.RequestMessage.Method == "POST").Should().BeEmpty();
        // The near-miss is recorded as a RACE_RECOVERED decision.
        (await ctx.Decisions.AnyAsync(d => d.ClientId == clientId && d.Reason!.Contains("RACE_RECOVERED")))
            .Should().BeTrue();
    }

    // ── The pause actually executes when the verdict still says pause ──

    [Fact]
    public async Task Approved_pause_is_superseded_without_a_vendor_write_after_client_is_demoted_to_shadow()
    {
        var clientId = EnforcementSeed.SeedMappedClient(
            _db,
            subscriptionStatus: "canceled",
            campaignEffectiveStatus: "ACTIVE",
            Now);
        var actionId = EnforcementSeed.SeedApprovedPause(
            _db,
            clientId,
            EnforcementSeed.CampaignId,
            expectedEpoch: 0,
            Now);
        await using (var demote = _db.CreateContext())
        {
            (await demote.Clients.FindAsync(clientId))!.EnforcementMode = EnforcementMode.Shadow;
            await demote.SaveChangesAsync();
        }

        await BuildDispatcher(new SafetyOptions { AllowProductionMetaWrites = true }).RunAsync(default);

        await using var verify = _db.CreateContext();
        (await verify.OutboxActions.FindAsync(actionId))!.Status.Should().Be(OutboxStatus.Superseded);
        _meta.LogEntries.Where(entry => entry.RequestMessage.Method == "POST").Should().BeEmpty();
        (await verify.PauseOperations.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Merge_committed_while_dispatch_waits_is_included_in_final_revalidation()
    {
        var survivorId = EnforcementSeed.SeedMappedClient(
            _db,
            subscriptionStatus: "active",
            campaignEffectiveStatus: "ACTIVE",
            Now);
        Guid duplicateId;
        await using (var seed = _db.CreateContext())
        {
            var survivor = (await seed.Clients.FindAsync(survivorId))!;
            survivor.ArrangementStatus = ArrangementStatus.Confirmed;
            survivor.ExpectedAmount = 100m;

            duplicateId = Guid.NewGuid();
            seed.Clients.Add(new Client
            {
                Id = duplicateId,
                BusinessName = "Duplicate with covering payment",
                ContractType = ContractType.Paid,
                AccountType = AccountType.Master,
                EnforcementMode = EnforcementMode.Shadow,
                CurrencyCode = "USD",
                CreatedAt = Now,
            });
            seed.LedgerEntries.AddRange(
                new LedgerEntry
                {
                    Id = Guid.NewGuid(),
                    ClientId = survivorId,
                    Type = LedgerEntryType.AdSpend,
                    OccurredAt = Now,
                    SignedAmount = -500m,
                    CurrencyCode = "USD",
                    SourceSystem = ExternalSystem.Meta,
                    SourceObjectId = "spend-before-merge",
                },
                new LedgerEntry
                {
                    Id = Guid.NewGuid(),
                    ClientId = duplicateId,
                    Type = LedgerEntryType.ChargePaid,
                    OccurredAt = Now,
                    SignedAmount = 500m,
                    CurrencyCode = "USD",
                    SourceSystem = ExternalSystem.Stripe,
                    SourceObjectId = "payment-on-duplicate",
                });
            await seed.SaveChangesAsync();
        }

        var actionId = EnforcementSeed.SeedApprovedPause(
            _db,
            survivorId,
            EnforcementSeed.CampaignId,
            expectedEpoch: 0,
            Now);
        _meta.Given(Request.Create().WithPath($"/{EnforcementSeed.CampaignId}").UsingGet())
            .InScenario("merge-crossing-pause")
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody($$"""{"id":"{{EnforcementSeed.CampaignId}}","status":"ACTIVE","effective_status":"ACTIVE"}"""));
        _meta.Given(Request.Create().WithPath($"/{EnforcementSeed.CampaignId}").UsingPost())
            .InScenario("merge-crossing-pause")
            .WillSetStateTo("paused")
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody("""{"success":true}"""));
        _meta.Given(Request.Create().WithPath($"/{EnforcementSeed.CampaignId}").UsingGet())
            .InScenario("merge-crossing-pause")
            .WhenStateIs("paused")
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody($$"""{"id":"{{EnforcementSeed.CampaignId}}","status":"PAUSED","effective_status":"PAUSED"}"""));

        var fenceCommandReached = new TaskCompletionSource<Guid>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFenceCommand = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        async Task BeforeClientFence(Guid clientId, CancellationToken ct)
        {
            fenceCommandReached.TrySetResult(clientId);
            await releaseFenceCommand.Task.WaitAsync(ct);
        }

        var dispatcher = BuildDispatcher(
            new SafetyOptions { AllowProductionMetaWrites = true },
            BeforeClientFence);

        var dispatchTask = dispatcher.RunAsync(default);
        var waitingClientId = await fenceCommandReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        waitingClientId.Should().Be(survivorId);

        try
        {
            var merge = await new ClientMergeService(_db.Factory, _clock)
                .MergeAsync(survivorId, duplicateId, "merge-test");
            merge.Ok.Should().BeTrue();
        }
        finally
        {
            releaseFenceCommand.TrySetResult();
        }
        await dispatchTask;

        await using var verify = _db.CreateContext();
        (await verify.OutboxActions.FindAsync(actionId))!.Status.Should().Be(OutboxStatus.Superseded);
        _meta.LogEntries.Where(entry => entry.RequestMessage.Method == "POST").Should().BeEmpty();
        (await verify.PauseOperations.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Approved_pause_executes_and_records_provenance_when_verdict_holds()
    {
        var clientId = EnforcementSeed.SeedMappedClient(_db, subscriptionStatus: "canceled", campaignEffectiveStatus: "ACTIVE", Now);
        var actionId = EnforcementSeed.SeedApprovedPause(_db, clientId, EnforcementSeed.CampaignId, expectedEpoch: 0, Now);

        // read-back: ACTIVE before the POST (scenario start), PAUSED after.
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

        await BuildDispatcher(new SafetyOptions { AllowProductionMetaWrites = true }).RunAsync(default);

        await using var ctx = _db.CreateContext();
        (await ctx.OutboxActions.FindAsync(actionId))!.Status.Should().Be(OutboxStatus.Executed);
        var op = await ctx.PauseOperations.SingleAsync(p => p.ClientId == clientId);
        op.ExternalId.Should().Be(EnforcementSeed.CampaignId);
        op.PriorStatus.Should().Be("ACTIVE");
        op.State.Should().Be(PauseOperationState.Paused);
    }

    // ── Safety gate: production writes disabled ⇒ a non-canary pause is refused, not attempted ──

    [Fact]
    public async Task Non_canary_pause_is_dead_lettered_when_production_writes_disabled()
    {
        var clientId = EnforcementSeed.SeedMappedClient(_db, subscriptionStatus: "canceled", campaignEffectiveStatus: "ACTIVE", Now);
        var actionId = EnforcementSeed.SeedApprovedPause(_db, clientId, EnforcementSeed.CampaignId, expectedEpoch: 0, Now);
        StubCampaignGet("ACTIVE", "ACTIVE"); // the pre-write read-back succeeds; the write itself is blocked

        await BuildDispatcher(new SafetyOptions { AllowProductionMetaWrites = false, CanaryCampaignId = "some_other_canary" }).RunAsync(default);

        await using var ctx = _db.CreateContext();
        (await ctx.OutboxActions.FindAsync(actionId))!.Status.Should().Be(OutboxStatus.DeadLettered);
        _meta.LogEntries.Where(e => e.RequestMessage.Method == "POST").Should().BeEmpty();
        (await ctx.PauseOperations.AnyAsync()).Should().BeFalse();
    }

    // ── Kill-switch epoch: an action staged under a stale epoch supersedes before any write ──

    [Fact]
    public async Task Action_with_stale_kill_switch_epoch_is_superseded()
    {
        var clientId = EnforcementSeed.SeedMappedClient(_db, subscriptionStatus: "canceled", campaignEffectiveStatus: "ACTIVE", Now);
        var actionId = EnforcementSeed.SeedApprovedPause(_db, clientId, EnforcementSeed.CampaignId, expectedEpoch: 0, Now);
        EnforcementSeed.SetKillSwitch(_db, enabled: false, epoch: 5, Now); // epoch moved since staging

        await BuildDispatcher(new SafetyOptions { AllowProductionMetaWrites = true }).RunAsync(default);

        await using var ctx = _db.CreateContext();
        (await ctx.OutboxActions.FindAsync(actionId))!.Status.Should().Be(OutboxStatus.Superseded);
        _meta.LogEntries.Where(e => e.RequestMessage.Method == "POST").Should().BeEmpty();
    }

    // ── Kill-switch engaged: the dispatcher does nothing at all ──

    [Fact]
    public async Task Engaged_kill_switch_halts_all_dispatch()
    {
        var clientId = EnforcementSeed.SeedMappedClient(_db, subscriptionStatus: "canceled", campaignEffectiveStatus: "ACTIVE", Now);
        var actionId = EnforcementSeed.SeedApprovedPause(_db, clientId, EnforcementSeed.CampaignId, expectedEpoch: 0, Now);
        EnforcementSeed.SetKillSwitch(_db, enabled: true, epoch: 0, Now);

        await BuildDispatcher(new SafetyOptions { AllowProductionMetaWrites = true }).RunAsync(default);

        await using var ctx = _db.CreateContext();
        // Untouched — still Approved, not even claimed.
        (await ctx.OutboxActions.FindAsync(actionId))!.Status.Should().Be(OutboxStatus.Approved);
    }

    public void Dispose()
    {
        _meta.Stop();
        _db.Dispose();
    }
}
