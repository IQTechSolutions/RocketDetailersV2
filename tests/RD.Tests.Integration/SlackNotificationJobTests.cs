using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RD.Domain;
using RD.Domain.Entities;
using RD.Infrastructure.Slack;
using RD.Tests.Integration.TestInfra;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace RD.Tests.Integration;

/// <summary>
/// The notifier posts each AwaitingApproval action to Slack exactly once:
/// SlackNotifiedAt is stamped after the post, so a later run never re-posts.
/// The webhook is a WireMock stub asserting the POST count.
/// </summary>
public sealed class SlackNotificationJobTests : IDisposable
{
    private const string HookPath = "/services/T000/B000/xxxxONETIMExxxx";
    private static readonly DateTimeOffset Now = new(2026, 07, 24, 12, 0, 0, TimeSpan.Zero);

    private readonly SyncTestDb _db = new();
    private readonly WireMockServer _server = WireMockServer.Start();
    private readonly TestClock _clock = new(Now);

    public void Dispose()
    {
        _server.Stop();
        _server.Dispose();
        _db.Dispose();
    }

    private SlackNotificationJob CreateJob(string? webhookUrl)
    {
        var options = Options.Create(new SlackOptions { IncomingWebhookUrl = webhookUrl ?? "" });
        var notifier = new SlackNotifier(new HttpClient(), options);
        return new SlackNotificationJob(_db.Factory, notifier, _clock, NullLogger<SlackNotificationJob>.Instance);
    }

    private void StubOkWebhook() =>
        _server.Given(Request.Create().WithPath(HookPath).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("ok"));

    private Guid SeedAwaitingAction(string businessName, string reason)
    {
        using var ctx = _db.CreateContext();
        var client = new Client
        {
            Id = Guid.NewGuid(), BusinessName = businessName,
            ContractType = ContractType.Paid, AccountType = AccountType.Master, CreatedAt = Now,
        };
        ctx.Clients.Add(client);

        var decision = new Decision
        {
            Id = Guid.NewGuid(), ClientId = client.Id, EvaluatedAt = Now,
            PolicyVersion = "v1", StateSnapshotJson = "{}",
            ProposedAction = ProposedActionType.Pause, Mode = EnforcementMode.Assist, Reason = reason,
        };
        ctx.Decisions.Add(decision);

        var action = new OutboxAction
        {
            Id = Guid.NewGuid(), ClientId = client.Id, DecisionId = decision.Id,
            ActionType = OutboxActionType.PauseCampaign,
            PayloadJson = "{\"CampaignIds\":[\"camp_1\"]}",
            IdempotencyKey = "PauseCampaign:" + Guid.NewGuid(),
            Status = OutboxStatus.AwaitingApproval, CreatedAt = Now,
        };
        ctx.OutboxActions.Add(action);
        ctx.SaveChanges();
        return action.Id;
    }

    [Fact]
    public async Task RunTwice_PostsExactlyOnce_AndStampsNotifiedAt()
    {
        var actionId = SeedAwaitingAction("Sparkle Detailing", "sub past_due, campaign ACTIVE");
        StubOkWebhook();

        var job = CreateJob(_server.Urls[0] + HookPath);
        await job.RunAsync(CancellationToken.None);
        await job.RunAsync(CancellationToken.None); // notify-once proof

        // Exactly one POST across two runs.
        _server.LogEntries.Should().HaveCount(1);

        // SlackNotifiedAt stamped once, to the run clock.
        await using var db = _db.CreateContext();
        var action = await db.OutboxActions.FindAsync(actionId);
        action!.SlackNotifiedAt.Should().Be(Now);

        // The Block Kit body carries the action id (button value) + the two action ids + the reason.
        var body = _server.LogEntries.Single().RequestMessage.Body ?? "";
        body.Should().Contain(actionId.ToString());
        body.Should().Contain("rd_approve");
        body.Should().Contain("rd_dismiss");
        body.Should().Contain("Sparkle Detailing");
        body.Should().Contain("sub past_due, campaign ACTIVE");
    }

    [Fact]
    public async Task AlreadyNotified_Action_IsNotReposted()
    {
        var actionId = SeedAwaitingAction("Already Notified Co", "reason");
        using (var ctx = _db.CreateContext())
        {
            var a = ctx.OutboxActions.Find(actionId)!;
            a.SlackNotifiedAt = Now.AddMinutes(-5); // pretend a prior run already posted it
            ctx.SaveChanges();
        }
        StubOkWebhook();

        await CreateJob(_server.Urls[0] + HookPath).RunAsync(CancellationToken.None);

        _server.LogEntries.Should().BeEmpty();
    }

    [Fact]
    public async Task UnconfiguredWebhook_IsNoOp_AndLeavesNotifiedAtNull()
    {
        var actionId = SeedAwaitingAction("No Webhook Co", "reason");

        await CreateJob(webhookUrl: null).RunAsync(CancellationToken.None);

        _server.LogEntries.Should().BeEmpty();
        await using var db = _db.CreateContext();
        var action = await db.OutboxActions.FindAsync(actionId);
        action!.SlackNotifiedAt.Should().BeNull();
    }
}
