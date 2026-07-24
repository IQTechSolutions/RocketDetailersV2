using FluentAssertions;
using RD.Domain;
using RD.Domain.Entities;
using RD.Infrastructure.Enforcement;
using RD.Tests.Integration.TestInfra;

namespace RD.Tests.Integration;

/// <summary>Approve/Dismiss compare-and-swap: exactly one transition wins; the loser gets AlreadyResolved.</summary>
public sealed class ApprovalServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 07, 24, 12, 0, 0, TimeSpan.Zero);
    private readonly SyncTestDb _db = new();
    private readonly ApprovalService _service;

    public ApprovalServiceTests() => _service = new ApprovalService(_db.Factory, new TestClock(Now));

    private Guid SeedAwaiting()
    {
        using var ctx = _db.CreateContext();
        var a = new OutboxAction
        {
            Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), DecisionId = Guid.Empty,
            ActionType = OutboxActionType.PauseCampaign, PayloadJson = "{\"CampaignIds\":[\"c1\"]}",
            IdempotencyKey = "k:" + Guid.NewGuid(), Status = OutboxStatus.AwaitingApproval, CreatedAt = Now,
        };
        ctx.OutboxActions.Add(a);
        ctx.SaveChanges();
        return a.Id;
    }

    [Fact]
    public async Task Approve_transitions_awaiting_to_approved()
    {
        var id = SeedAwaiting();
        (await _service.ApproveAsync(id, ApprovalChannel.Cockpit, "op")).Should().Be(ApprovalOutcome.Approved);

        await using var ctx = _db.CreateContext();
        var a = await ctx.OutboxActions.FindAsync(id);
        a!.Status.Should().Be(OutboxStatus.Approved);
        a.ApprovedBy.Should().Be("op");
        a.ApprovalChannel.Should().Be(ApprovalChannel.Cockpit);
        a.ActionVersion.Should().Be(2);
    }

    [Fact]
    public async Task Second_action_on_the_same_row_gets_already_resolved()
    {
        var id = SeedAwaiting();
        (await _service.ApproveAsync(id, ApprovalChannel.Cockpit, "op")).Should().Be(ApprovalOutcome.Approved);
        // Slack tries to dismiss the same, already-approved action.
        (await _service.DismissAsync(id, ApprovalChannel.Slack, "op2", "changed mind")).Should().Be(ApprovalOutcome.AlreadyResolved);
    }

    [Fact]
    public async Task Dismiss_records_reason()
    {
        var id = SeedAwaiting();
        (await _service.DismissAsync(id, ApprovalChannel.Slack, "op", "not a real failure")).Should().Be(ApprovalOutcome.Dismissed);

        await using var ctx = _db.CreateContext();
        var a = await ctx.OutboxActions.FindAsync(id);
        a!.Status.Should().Be(OutboxStatus.Dismissed);
        a.DismissReason.Should().Be("not a real failure");
    }

    [Fact]
    public async Task Missing_action_returns_not_found()
        => (await _service.ApproveAsync(Guid.NewGuid(), ApprovalChannel.Cockpit, "op")).Should().Be(ApprovalOutcome.NotFound);

    public void Dispose() => _db.Dispose();
}
