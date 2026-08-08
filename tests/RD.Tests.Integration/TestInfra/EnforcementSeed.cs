using RD.Domain;
using RD.Domain.Entities;
using System.Text.Json;

namespace RD.Tests.Integration.TestInfra;

/// <summary>Seeds a fully-mapped, verified master client + projections + an Approved outbox action, for dispatcher tests.</summary>
public static class EnforcementSeed
{
    public const string CustomerId = "cus_test1";
    public const string SubscriptionId = "sub_test1";
    public const string CampaignId = "camp_test1";
    public const string ContactId = "contact_test1";

    /// <summary>Creates the client (Assist), its four links, a current mapping verification, and vendor projections.</summary>
    public static Guid SeedMappedClient(SyncTestDb db, string subscriptionStatus, string campaignEffectiveStatus, DateTimeOffset now)
    {
        using var ctx = db.CreateContext();
        var client = new Client
        {
            Id = Guid.NewGuid(), BusinessName = "Mapped Detailing Co",
            ContractType = ContractType.Paid, AccountType = AccountType.Master,
            EnforcementMode = EnforcementMode.Assist, CurrencyCode = "USD", CreatedAt = now,
        };
        ctx.Clients.Add(client);

        var pins = new List<object>();
        void Link(ExternalSystem s, LinkKind k, string ext)
        {
            var link = new IdentityLink
            {
                Id = Guid.NewGuid(), ClientId = client.Id, System = s, Kind = k, ExternalId = ext,
                VerifiedAt = now, CreatedAt = now,
            };
            ctx.IdentityLinks.Add(link);
            pins.Add(new { linkId = link.Id, linkVersion = link.LinkVersion });
        }
        Link(ExternalSystem.Stripe, LinkKind.Customer, CustomerId);
        Link(ExternalSystem.Stripe, LinkKind.Subscription, SubscriptionId);
        Link(ExternalSystem.Meta, LinkKind.Campaign, CampaignId);
        Link(ExternalSystem.Ghl, LinkKind.Contact, ContactId);

        ctx.MappingVerifications.Add(new MappingVerification
        {
            Id = Guid.NewGuid(), ClientId = client.Id, VerifiedLinksJson = JsonSerializer.Serialize(pins),
            VerifiedBy = "test", BlastRadiusAcknowledged = true, VerifiedAt = now,
        });

        ctx.StripeSubscriptions.Add(new StripeSubscriptionProj
        {
            SubscriptionId = SubscriptionId, ClientId = client.Id, CustomerId = CustomerId,
            Status = subscriptionStatus, SourceSyncedAt = now,
        });
        ctx.MetaCampaigns.Add(new MetaCampaignProj
        {
            CampaignId = CampaignId, ClientId = client.Id, AdAccountId = "act_1", Name = "Test",
            Status = campaignEffectiveStatus, EffectiveStatus = campaignEffectiveStatus, SourceSyncedAt = now,
        });

        // Fresh syncs so the freshness gate does not downgrade enforcement.
        ctx.SyncRuns.Add(new SyncRun { Id = Guid.NewGuid(), System = ExternalSystem.Stripe, StartedAt = now, CompletedAt = now, Status = SyncRunStatus.Completed });
        ctx.SyncRuns.Add(new SyncRun { Id = Guid.NewGuid(), System = ExternalSystem.Meta, StartedAt = now, CompletedAt = now, Status = SyncRunStatus.Completed });

        ctx.SaveChanges();
        return client.Id;
    }

    public static Guid SeedApprovedPause(SyncTestDb db, Guid clientId, string campaignId, long expectedEpoch, DateTimeOffset now)
    {
        using var ctx = db.CreateContext();
        var action = new OutboxAction
        {
            Id = Guid.NewGuid(), ClientId = clientId, DecisionId = Guid.Empty,
            ActionType = OutboxActionType.PauseCampaign,
            PayloadJson = $"{{\"CampaignIds\":[\"{campaignId}\"]}}",
            IdempotencyKey = $"PauseCampaign:{clientId}:{campaignId}",
            Status = OutboxStatus.Approved, ExpectedKillSwitchEpoch = expectedEpoch, CreatedAt = now,
        };
        ctx.OutboxActions.Add(action);
        ctx.SaveChanges();
        return action.Id;
    }

    public static void SetKillSwitch(SyncTestDb db, bool enabled, long epoch, DateTimeOffset now)
    {
        using var ctx = db.CreateContext();
        ctx.KillSwitch.Add(new KillSwitchState { Id = 1, Enabled = enabled, Epoch = epoch, ChangedAt = now });
        ctx.SaveChanges();
    }
}
