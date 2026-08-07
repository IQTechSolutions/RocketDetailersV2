using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RD.Domain;
using RD.Domain.Entities;
using RD.Domain.Policy;
using RD.Infrastructure.Enforcement;
using RD.Infrastructure.Sync;
using RD.Tests.Integration.TestInfra;

namespace RD.Tests.Integration;

public sealed class PolicyEvaluationJobTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 08, 07, 12, 0, 0, TimeSpan.Zero);
    private readonly SyncTestDb _db = new();
    private readonly TestClock _clock = new(Now);

    [Fact]
    public async Task Policy_external_pause_investigation_closes_when_fresh_meta_shows_campaign_active()
    {
        var clientId = EnforcementSeed.SeedMappedClient(
            _db, subscriptionStatus: "active", campaignEffectiveStatus: "PAUSED", Now);
        var job = CreateJob();
        var freshAt = Now.AddMinutes(5);

        await job.RunAsync(CancellationToken.None);

        Guid policyItemId;
        await using (var seed = _db.CreateContext())
        {
            var policyItem = await seed.InvestigationItems.SingleAsync();
            policyItem.Detail.Should().Be(EligibilityPolicy.ExternallyPausedPaymentReason);
            policyItemId = policyItem.Id;

            // Same kind, different detail: this represents the legacy Stripe-import
            // classification and must never be closed by policy reconciliation.
            seed.InvestigationItems.Add(new InvestigationItem
            {
                Id = Guid.NewGuid(),
                ClientId = clientId,
                Kind = InvestigationKind.ExternallyPausedPayment,
                Detail = "At least one Stripe customer here is delinquent (unpaid invoice).",
                CreatedAt = Now.AddMinutes(1),
            });
            // SQL Server's default collation is case-insensitive. A near-match must
            // still remain operator-owned rather than being treated as this policy's row.
            seed.InvestigationItems.Add(new InvestigationItem
            {
                Id = Guid.NewGuid(),
                ClientId = clientId,
                Kind = InvestigationKind.ExternallyPausedPayment,
                Detail = "client is paid up but a campaign was paused outside the app — never auto-resume someone else's pause.",
                CreatedAt = Now.AddMinutes(1),
            });
            // Same detail, different kind: the resolver must also remain kind-scoped.
            seed.InvestigationItems.Add(new InvestigationItem
            {
                Id = Guid.NewGuid(),
                ClientId = clientId,
                Kind = InvestigationKind.Other,
                Detail = EligibilityPolicy.ExternallyPausedPaymentReason,
                CreatedAt = Now.AddMinutes(1),
            });

            var campaign = await seed.MetaCampaigns.SingleAsync(c => c.CampaignId == EnforcementSeed.CampaignId);
            campaign.Status = "ACTIVE";
            campaign.EffectiveStatus = "ACTIVE";
            campaign.SourceSyncedAt = freshAt;
            seed.SyncRuns.Add(CompletedSync(ExternalSystem.Meta, freshAt));
            await seed.SaveChangesAsync();
        }

        _clock.UtcNow = freshAt;
        await job.RunAsync(CancellationToken.None);

        await using var assert = _db.CreateContext();
        var policyItemAfter = await assert.InvestigationItems.SingleAsync(i => i.Id == policyItemId);
        policyItemAfter.Status.Should().Be(InvestigationStatus.Resolved);
        policyItemAfter.ResolvedAt.Should().Be(_clock.UtcNow);
        policyItemAfter.ResolvedBy.Should().Be("policy-evaluation");
        policyItemAfter.ResolutionNote.Should().NotBeNullOrWhiteSpace();

        var untouched = await assert.InvestigationItems
            .Where(i => i.Id != policyItemId)
            .ToListAsync();
        untouched.Should().HaveCount(3);
        untouched.Should().OnlyContain(i =>
            i.Status == InvestigationStatus.Open
            && i.ResolvedAt == null
            && i.ResolvedBy == null
            && i.ResolutionNote == null);
    }

    [Fact]
    public async Task Policy_external_pause_investigation_stays_open_without_duplicates_while_verdict_still_reports_it()
    {
        EnforcementSeed.SeedMappedClient(
            _db, subscriptionStatus: "active", campaignEffectiveStatus: "PAUSED", Now);
        var job = CreateJob();

        await job.RunAsync(CancellationToken.None);
        _clock.UtcNow = Now.AddMinutes(5);
        await job.RunAsync(CancellationToken.None);

        await using var assert = _db.CreateContext();
        var items = await assert.InvestigationItems
            .Where(i => i.Kind == InvestigationKind.ExternallyPausedPayment
                        && i.Detail == EligibilityPolicy.ExternallyPausedPaymentReason)
            .ToListAsync();
        items.Should().ContainSingle();
        items.Single().Status.Should().Be(InvestigationStatus.Open);
        items.Single().ResolvedAt.Should().BeNull();
        items.Single().ResolvedBy.Should().BeNull();
        items.Single().ResolutionNote.Should().BeNull();
    }

    [Fact]
    public async Task Policy_external_pause_investigation_closes_when_fresh_stripe_shows_canceled_even_if_campaign_stays_paused()
    {
        var (_, policyItemId) = await SeedOpenPolicyExternalPauseAsync();
        var freshAt = Now.AddMinutes(5);

        await using (var seed = _db.CreateContext())
        {
            var subscription = await seed.StripeSubscriptions.SingleAsync();
            subscription.Status = "canceled";
            subscription.SourceSyncedAt = freshAt;
            seed.SyncRuns.Add(CompletedSync(ExternalSystem.Stripe, freshAt));
            seed.SyncRuns.Add(CompletedSync(ExternalSystem.Meta, freshAt));
            await seed.SaveChangesAsync();
        }

        _clock.UtcNow = freshAt;
        await CreateJob().RunAsync(CancellationToken.None);

        await using var assert = _db.CreateContext();
        var item = await assert.InvestigationItems.SingleAsync(i => i.Id == policyItemId);
        item.Status.Should().Be(InvestigationStatus.Resolved);
        item.ResolutionNote.Should().Contain("canceled");
    }

    [Fact]
    public async Task Higher_priority_non_usd_verdict_does_not_close_a_still_valid_external_pause()
    {
        var (clientId, policyItemId) = await SeedOpenPolicyExternalPauseAsync();

        await using (var seed = _db.CreateContext())
        {
            (await seed.Clients.SingleAsync(c => c.Id == clientId)).CurrencyCode = "CAD";
            await seed.SaveChangesAsync();
        }

        _clock.UtcNow = Now.AddMinutes(5);
        await CreateJob().RunAsync(CancellationToken.None);

        await AssertStillOpenAsync(policyItemId);
    }

    [Fact]
    public async Task Higher_priority_app_resume_verdict_does_not_close_another_external_pause()
    {
        var (clientId, policyItemId) = await SeedOpenPolicyExternalPauseAsync();
        var freshAt = Now.AddMinutes(5);
        const string appPausedCampaignId = "camp_app_paused";

        await using (var seed = _db.CreateContext())
        {
            seed.IdentityLinks.Add(new IdentityLink
            {
                Id = Guid.NewGuid(), ClientId = clientId, System = ExternalSystem.Meta,
                Kind = LinkKind.Campaign, ExternalId = appPausedCampaignId,
                VerifiedAt = freshAt, CreatedAt = freshAt,
            });
            seed.MetaCampaigns.Add(new MetaCampaignProj
            {
                CampaignId = appPausedCampaignId, ClientId = clientId, AdAccountId = "act_1",
                Name = "App paused", Status = "PAUSED", EffectiveStatus = "PAUSED",
                SourceSyncedAt = freshAt,
            });
            seed.PauseOperations.Add(new PauseOperation
            {
                Id = Guid.NewGuid(), ClientId = clientId, OutboxActionId = Guid.NewGuid(),
                EntityType = MetaEntityType.Campaign, ExternalId = appPausedCampaignId,
                PriorStatus = "ACTIVE", State = PauseOperationState.Paused, PausedAt = freshAt,
            });
            seed.SyncRuns.Add(CompletedSync(ExternalSystem.Meta, freshAt));
            await seed.SaveChangesAsync();
        }

        _clock.UtcNow = freshAt;
        await CreateJob().RunAsync(CancellationToken.None);

        await AssertStillOpenAsync(policyItemId);
    }

    [Fact]
    public async Task Active_campaign_does_not_close_the_investigation_when_meta_evidence_is_stale()
    {
        var (_, policyItemId) = await SeedOpenPolicyExternalPauseAsync();

        await using (var seed = _db.CreateContext())
        {
            var campaign = await seed.MetaCampaigns.SingleAsync();
            campaign.Status = "ACTIVE";
            campaign.EffectiveStatus = "ACTIVE";
            await seed.SaveChangesAsync();
        }

        _clock.UtcNow = Now + EligibilityPolicy.StalenessBound + TimeSpan.FromMinutes(1);
        await CreateJob().RunAsync(CancellationToken.None);

        await AssertStillOpenAsync(policyItemId);
    }

    [Fact]
    public async Task Canceled_subscription_does_not_close_the_investigation_when_stripe_evidence_is_stale()
    {
        var (_, policyItemId) = await SeedOpenPolicyExternalPauseAsync();
        var evaluatedAt = Now + EligibilityPolicy.StalenessBound + TimeSpan.FromMinutes(1);

        await using (var seed = _db.CreateContext())
        {
            (await seed.StripeSubscriptions.SingleAsync()).Status = "canceled";
            var campaign = await seed.MetaCampaigns.SingleAsync();
            campaign.SourceSyncedAt = evaluatedAt;
            seed.SyncRuns.Add(CompletedSync(ExternalSystem.Meta, evaluatedAt));
            await seed.SaveChangesAsync();
        }

        _clock.UtcNow = evaluatedAt;
        await CreateJob().RunAsync(CancellationToken.None);

        await AssertStillOpenAsync(policyItemId);
    }

    [Fact]
    public async Task Missing_campaign_projection_is_not_treated_as_evidence_that_the_pause_cleared()
    {
        var (_, policyItemId) = await SeedOpenPolicyExternalPauseAsync();
        var freshAt = Now.AddMinutes(5);

        await using (var seed = _db.CreateContext())
        {
            seed.MetaCampaigns.Remove(await seed.MetaCampaigns.SingleAsync());
            seed.SyncRuns.Add(CompletedSync(ExternalSystem.Meta, freshAt));
            await seed.SaveChangesAsync();
        }

        _clock.UtcNow = freshAt;
        await CreateJob().RunAsync(CancellationToken.None);

        await AssertStillOpenAsync(policyItemId);
    }

    [Fact]
    public async Task Operator_resolved_and_dismissed_audit_fields_are_preserved()
    {
        var (clientId, resolvedId) = await SeedOpenPolicyExternalPauseAsync();
        var operatorAt = Now.AddMinutes(2);
        var freshAt = Now.AddMinutes(5);
        var dismissedId = Guid.NewGuid();

        await using (var seed = _db.CreateContext())
        {
            var resolved = await seed.InvestigationItems.SingleAsync(i => i.Id == resolvedId);
            resolved.Status = InvestigationStatus.Resolved;
            resolved.ResolvedAt = operatorAt;
            resolved.ResolvedBy = "operator-a";
            resolved.ResolutionNote = "Verified manually.";
            seed.InvestigationItems.Add(new InvestigationItem
            {
                Id = dismissedId, ClientId = clientId,
                Kind = InvestigationKind.ExternallyPausedPayment,
                Detail = EligibilityPolicy.ExternallyPausedPaymentReason,
                Status = InvestigationStatus.Dismissed, CreatedAt = Now,
                ResolvedAt = operatorAt, ResolvedBy = "operator-b",
                ResolutionNote = "Dismissed manually.",
            });
            var campaign = await seed.MetaCampaigns.SingleAsync();
            campaign.Status = "ACTIVE";
            campaign.EffectiveStatus = "ACTIVE";
            campaign.SourceSyncedAt = freshAt;
            seed.SyncRuns.Add(CompletedSync(ExternalSystem.Meta, freshAt));
            await seed.SaveChangesAsync();
        }

        _clock.UtcNow = freshAt;
        await CreateJob().RunAsync(CancellationToken.None);

        await using var assert = _db.CreateContext();
        var resolvedAfter = await assert.InvestigationItems.SingleAsync(i => i.Id == resolvedId);
        resolvedAfter.Status.Should().Be(InvestigationStatus.Resolved);
        resolvedAfter.ResolvedAt.Should().Be(operatorAt);
        resolvedAfter.ResolvedBy.Should().Be("operator-a");
        resolvedAfter.ResolutionNote.Should().Be("Verified manually.");
        var dismissedAfter = await assert.InvestigationItems.SingleAsync(i => i.Id == dismissedId);
        dismissedAfter.Status.Should().Be(InvestigationStatus.Dismissed);
        dismissedAfter.ResolvedAt.Should().Be(operatorAt);
        dismissedAfter.ResolvedBy.Should().Be("operator-b");
        dismissedAfter.ResolutionNote.Should().Be("Dismissed manually.");
    }

    private async Task<(Guid ClientId, Guid PolicyItemId)> SeedOpenPolicyExternalPauseAsync()
    {
        var clientId = EnforcementSeed.SeedMappedClient(
            _db, subscriptionStatus: "active", campaignEffectiveStatus: "PAUSED", Now);
        await CreateJob().RunAsync(CancellationToken.None);

        await using var assert = _db.CreateContext();
        var item = await assert.InvestigationItems.SingleAsync(i =>
            i.Kind == InvestigationKind.ExternallyPausedPayment
            && i.Detail == EligibilityPolicy.ExternallyPausedPaymentReason);
        item.Status.Should().Be(InvestigationStatus.Open);
        return (clientId, item.Id);
    }

    private async Task AssertStillOpenAsync(Guid itemId)
    {
        await using var assert = _db.CreateContext();
        var item = await assert.InvestigationItems.SingleAsync(i => i.Id == itemId);
        item.Status.Should().Be(InvestigationStatus.Open);
        item.ResolvedAt.Should().BeNull();
        item.ResolvedBy.Should().BeNull();
        item.ResolutionNote.Should().BeNull();
    }

    private static SyncRun CompletedSync(ExternalSystem system, DateTimeOffset at) => new()
    {
        Id = Guid.NewGuid(), System = system, StartedAt = at, CompletedAt = at,
        Status = SyncRunStatus.Completed,
    };

    private PolicyEvaluationJob CreateJob()
    {
        var options = Options.Create(new EnforcementOptions());
        return new PolicyEvaluationJob(
            _db.Factory,
            new ClientStateBuilder(_clock, options),
            new ActionStager(_clock, options),
            _clock,
            NullLogger<PolicyEvaluationJob>.Instance);
    }

    public void Dispose() => _db.Dispose();
}
