using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RD.Domain;
using RD.Domain.Entities;
using RD.Domain.Policy;
using RD.Infrastructure.Enforcement;
using RD.Tests.Integration.TestInfra;

namespace RD.Tests.Integration;

/// <summary>
/// Staging guardrails: the same verdict never stages twice (heartbeats are
/// idempotent), and a dunning step that cannot be sent honestly (no hosted
/// invoice URL / no GHL contact) routes to the work queue instead of faking
/// a send.
/// </summary>
public sealed class ActionStagerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 07, 24, 12, 0, 0, TimeSpan.Zero);
    private readonly SyncTestDb _db = new();
    private readonly TestClock _clock = new(Now);

    public void Dispose() => _db.Dispose();

    private ActionStager CreateStager() => new(_clock, Options.Create(new EnforcementOptions
    {
        DunningWorkflowId = "wf_dunning_1",
        DefaultDunningLocationId = "loc_1",
    }));

    private Client SeedAssistClient()
    {
        using var ctx = _db.CreateContext();
        var client = new Client
        {
            Id = Guid.NewGuid(), BusinessName = "Stager Detailing Co",
            ContractType = ContractType.Paid, AccountType = AccountType.Master,
            EnforcementMode = EnforcementMode.Assist, CreatedAt = Now,
        };
        ctx.Clients.Add(client);
        ctx.SaveChanges();
        return client;
    }

    private static ClientState StateFor(Client client) => new()
    {
        ClientId = client.Id, Mode = client.EnforcementMode,
        Contract = client.ContractType, Account = client.AccountType,
        EvaluatedAt = Now,
    };

    // ── Idempotency: the same pause verdict on two heartbeats stages exactly one action ──

    [Fact]
    public async Task Same_pause_verdict_staged_twice_yields_exactly_one_open_action()
    {
        var client = SeedAssistClient();
        var verdict = new PolicyDecision(ProposedActionType.Pause, "Subscription canceled.",
            TargetCampaignIds: ["camp_A", "camp_B"]);
        var stager = CreateStager();

        // Two heartbeats, each its own unit of work (caller owns SaveChanges).
        for (var heartbeat = 0; heartbeat < 2; heartbeat++)
        {
            await using var db = _db.CreateContext();
            await stager.StageAsync(db, client, StateFor(client), verdict, killSwitchEpoch: 7, CancellationToken.None);
            await db.SaveChangesAsync();
        }

        await using var ctx = _db.CreateContext();
        var action = await ctx.OutboxActions.SingleAsync(); // ONE action, not two
        action.ActionType.Should().Be(OutboxActionType.PauseCampaign);
        action.Status.Should().Be(OutboxStatus.AwaitingApproval);       // Assist queues for approval
        action.IdempotencyKey.Should().Be($"PauseCampaign:{client.Id}:camp_A,camp_B");
        action.ExpectedKillSwitchEpoch.Should().Be(7);
        action.PayloadJson.Should().Contain("camp_A").And.Contain("camp_B");
    }

    // ── No hosted invoice URL ⇒ investigation, never a fake dun ──

    [Fact]
    public async Task Dunning_step_without_hosted_invoice_url_routes_to_investigation_not_outbox()
    {
        var client = SeedAssistClient();
        await using (var seed = _db.CreateContext())
        {
            // The GHL contact exists; the missing piece is the invoice URL.
            seed.IdentityLinks.Add(new IdentityLink
            {
                Id = Guid.NewGuid(), ClientId = client.Id, System = ExternalSystem.Ghl,
                Kind = LinkKind.Contact, ExternalId = "cont_1", VerifiedAt = Now, CreatedAt = Now,
            });
            // An open invoice was synced WITHOUT a hosted URL — cannot dun honestly.
            seed.StripeInvoices.Add(new StripeInvoiceProj
            {
                InvoiceId = "in_open_1", ClientId = client.Id, CustomerId = "cus_1",
                Status = "open", HostedInvoiceUrl = null, CreatedAtSource = Now, SourceSyncedAt = Now,
            });
            await seed.SaveChangesAsync();
        }
        var verdict = new PolicyDecision(ProposedActionType.DunningStep, "Failed charge.", DunningStep: 1);

        await using (var db = _db.CreateContext())
        {
            await CreateStager().StageAsync(db, client, StateFor(client), verdict, killSwitchEpoch: 0, CancellationToken.None);
            await db.SaveChangesAsync();
        }

        await using var ctx = _db.CreateContext();
        (await ctx.OutboxActions.AnyAsync()).Should().BeFalse();        // nothing staged
        (await ctx.DunningCases.AnyAsync()).Should().BeFalse();         // no case opened either
        var item = await ctx.InvestigationItems.SingleAsync();
        item.ClientId.Should().Be(client.Id);
        item.Kind.Should().Be(InvestigationKind.DeliveryUnverified);
        item.Status.Should().Be(InvestigationStatus.Open);
        item.Detail.Should().Contain("no hosted invoice URL");
    }

    // ── No GHL contact link ⇒ investigation, never a fake dun ──

    [Fact]
    public async Task Dunning_step_without_ghl_contact_routes_to_investigation_not_outbox()
    {
        var client = SeedAssistClient();
        await using (var seed = _db.CreateContext())
        {
            // The invoice URL exists; the missing piece is the contact.
            seed.StripeInvoices.Add(new StripeInvoiceProj
            {
                InvoiceId = "in_open_2", ClientId = client.Id, CustomerId = "cus_1", Status = "open",
                HostedInvoiceUrl = "https://invoice.stripe.com/i/in_open_2", CreatedAtSource = Now, SourceSyncedAt = Now,
            });
            await seed.SaveChangesAsync();
        }
        var verdict = new PolicyDecision(ProposedActionType.DunningStep, "Failed charge.", DunningStep: 1);

        await using (var db = _db.CreateContext())
        {
            await CreateStager().StageAsync(db, client, StateFor(client), verdict, killSwitchEpoch: 0, CancellationToken.None);
            await db.SaveChangesAsync();
        }

        await using var ctx = _db.CreateContext();
        (await ctx.OutboxActions.AnyAsync()).Should().BeFalse();
        (await ctx.DunningCases.AnyAsync()).Should().BeFalse();
        var item = await ctx.InvestigationItems.SingleAsync();
        item.Kind.Should().Be(InvestigationKind.DeliveryUnverified);
        item.Detail.Should().Contain("no GHL contact is linked");
    }
}
