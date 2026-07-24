using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RD.Domain;
using RD.Domain.Entities;
using RD.Infrastructure.Enforcement;
using RD.Tests.Integration.TestInfra;

namespace RD.Tests.Integration;

/// <summary>The gated Assist→Auto promotion, re-checked at execution time against the live decision log.</summary>
public sealed class PromotionServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 08, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly SyncTestDb _db = new();
    private readonly PromotionService _service;

    public PromotionServiceTests()
        => _service = new PromotionService(_db.Factory, new TestClock(Now), Options.Create(new EnforcementOptions()));

    /// <summary>Seeds a mapped Assist client. When <paramref name="exercised"/>, adds an Executed action; adds one clean Assist decision at <paramref name="assistDaysAgo"/>.</summary>
    private Guid SeedAssist(bool exercised, int assistDaysAgo)
    {
        var clientId = EnforcementSeed.SeedMappedClient(_db, subscriptionStatus: "active", campaignEffectiveStatus: "ACTIVE", Now);
        using var ctx = _db.CreateContext();
        ctx.Decisions.Add(new Decision
        {
            Id = Guid.NewGuid(), ClientId = clientId, EvaluatedAt = Now.AddDays(-assistDaysAgo),
            PolicyVersion = "1.0.0", StateSnapshotJson = "{}", ProposedAction = ProposedActionType.None,
            Mode = EnforcementMode.Assist, Reason = "Paid up.",
        });
        if (exercised)
            ctx.OutboxActions.Add(new OutboxAction
            {
                Id = Guid.NewGuid(), ClientId = clientId, DecisionId = Guid.Empty,
                ActionType = OutboxActionType.PauseCampaign, PayloadJson = "{}",
                IdempotencyKey = "ex:" + Guid.NewGuid(), Status = OutboxStatus.Executed,
                CreatedAt = Now.AddDays(-assistDaysAgo), ExecutedAt = Now.AddDays(-assistDaysAgo),
            });
        ctx.SaveChanges();
        return clientId;
    }

    [Fact]
    public async Task A_clean_exercised_assist_client_is_promoted_to_auto()
    {
        var clientId = SeedAssist(exercised: true, assistDaysAgo: 20);

        var (result, assessment) = await _service.PromoteToAutoAsync(clientId);

        result.Should().Be(PromotionResult.Promoted);
        assessment!.CleanDayStreak.Should().Be(14);
        await using var ctx = _db.CreateContext();
        (await ctx.Clients.FindAsync(clientId))!.EnforcementMode.Should().Be(EnforcementMode.Auto);
    }

    [Fact]
    public async Task An_unexercised_client_is_blocked_and_stays_assist()
    {
        var clientId = SeedAssist(exercised: false, assistDaysAgo: 20);

        var (result, assessment) = await _service.PromoteToAutoAsync(clientId);

        result.Should().Be(PromotionResult.Blocked);
        assessment!.Blockers.Should().Contain(b => b.Contains("exercised"));
        await using var ctx = _db.CreateContext();
        (await ctx.Clients.FindAsync(clientId))!.EnforcementMode.Should().Be(EnforcementMode.Assist);
    }

    [Fact]
    public async Task A_recently_promoted_assist_client_has_too_short_a_streak()
    {
        var clientId = SeedAssist(exercised: true, assistDaysAgo: 5);
        (await _service.PromoteToAutoAsync(clientId)).Result.Should().Be(PromotionResult.Blocked);
    }

    [Fact]
    public async Task An_investigation_decision_breaks_the_streak()
    {
        var clientId = SeedAssist(exercised: true, assistDaysAgo: 20);
        using (var ctx = _db.CreateContext())
        {
            ctx.Decisions.Add(new Decision
            {
                Id = Guid.NewGuid(), ClientId = clientId, EvaluatedAt = Now.AddDays(-2),
                PolicyVersion = "1.0.0", StateSnapshotJson = "{}", ProposedAction = ProposedActionType.Investigate,
                Mode = EnforcementMode.Assist, Reason = "Something needed a human.",
            });
            ctx.SaveChanges();
        }
        var (result, assessment) = await _service.PromoteToAutoAsync(clientId);
        result.Should().Be(PromotionResult.Blocked);
        assessment!.CleanDayStreak.Should().Be(2); // today, -1 clean; -2 unclean
    }

    [Fact]
    public async Task Demote_returns_an_auto_client_to_assist()
    {
        var clientId = SeedAssist(exercised: true, assistDaysAgo: 20);
        await _service.PromoteToAutoAsync(clientId);

        await _service.DemoteToAssistAsync(clientId);

        await using var ctx = _db.CreateContext();
        (await ctx.Clients.FindAsync(clientId))!.EnforcementMode.Should().Be(EnforcementMode.Assist);
    }

    public void Dispose() => _db.Dispose();
}
