using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using RD.Domain;
using RD.Domain.Entities;
using RD.Infrastructure.Gateways;
using RD.Tests.Integration.TestInfra;
using RD.Web.Services;

namespace RD.Tests.Integration;

public sealed class ClientDirectoryMappingHealthTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 7, 21, 0, 0, TimeSpan.Zero);
    private readonly SyncTestDb _db = new();

    [Fact]
    public async Task Detail_reports_mapping_unverified_when_a_legacy_snapshot_omits_an_active_required_link()
    {
        var clientId = Guid.NewGuid();
        var links = new[]
        {
            Link(clientId, ExternalSystem.Stripe, LinkKind.Customer, "cus_a"),
            Link(clientId, ExternalSystem.Stripe, LinkKind.Customer, "cus_b"),
            Link(clientId, ExternalSystem.Stripe, LinkKind.Subscription, "sub_a"),
            Link(clientId, ExternalSystem.Meta, LinkKind.Campaign, "campaign_a"),
            Link(clientId, ExternalSystem.Ghl, LinkKind.Contact, "contact_a"),
        };

        await using (var seed = _db.CreateContext())
        {
            seed.Clients.Add(new Client
            {
                Id = clientId,
                BusinessName = "Legacy verification",
                AccountType = AccountType.Master,
                ContractType = ContractType.Paid,
                CreatedAt = Now,
            });
            seed.IdentityLinks.AddRange(links);
            seed.MappingVerifications.Add(new MappingVerification
            {
                Id = Guid.NewGuid(),
                ClientId = clientId,
                VerifiedLinksJson = JsonSerializer.Serialize(
                    links.Where(l => l.ExternalId != "cus_b")
                        .Select(l => new { linkId = l.Id, linkVersion = l.LinkVersion })),
                VerifiedBy = "legacy-import",
                BlastRadiusAcknowledged = true,
                VerifiedAt = Now,
            });
            await seed.SaveChangesAsync();
        }

        var service = new ClientDirectoryService(_db.Factory, CreateVendorLinks());
        var detail = await service.GetDetailAsync(clientId);

        detail.Should().NotBeNull();
        detail!.Health.Issues.Should().ContainSingle(issue =>
            issue.Title == "Mapping not verified" && issue.FixHref == $"/mapping?clientId={clientId}");
    }

    [Fact]
    public async Task Analytics_does_not_count_a_legacy_snapshot_that_omits_an_active_required_link()
    {
        var clientId = Guid.NewGuid();
        var links = new[]
        {
            Link(clientId, ExternalSystem.Stripe, LinkKind.Customer, "cus_a"),
            Link(clientId, ExternalSystem.Stripe, LinkKind.Customer, "cus_b"),
            Link(clientId, ExternalSystem.Stripe, LinkKind.Subscription, "sub_a"),
            Link(clientId, ExternalSystem.Meta, LinkKind.Campaign, "campaign_a"),
            Link(clientId, ExternalSystem.Ghl, LinkKind.Contact, "contact_a"),
        };

        await using (var seed = _db.CreateContext())
        {
            seed.Clients.Add(new Client
            {
                Id = clientId,
                BusinessName = "Legacy analytics verification",
                AccountType = AccountType.Master,
                ContractType = ContractType.Paid,
                CreatedAt = Now,
            });
            seed.IdentityLinks.AddRange(links);
            seed.MappingVerifications.Add(new MappingVerification
            {
                Id = Guid.NewGuid(),
                ClientId = clientId,
                VerifiedLinksJson = JsonSerializer.Serialize(
                    links.Where(link => link.ExternalId != "cus_b")
                        .Select(link => new { linkId = link.Id, linkVersion = link.LinkVersion })),
                VerifiedBy = "legacy-import",
                BlastRadiusAcknowledged = true,
                VerifiedAt = Now,
            });
            await seed.SaveChangesAsync();
        }

        var data = await new AnalyticsService(_db.Factory, new TestClock(Now)).LoadAsync();

        data.MasterClientCount.Should().Be(1);
        data.VerifiedMasterClientCount.Should().Be(0);
    }

    private static IdentityLink Link(
        Guid clientId, ExternalSystem system, LinkKind kind, string externalId) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        System = system,
        Kind = kind,
        ExternalId = externalId,
        VerifiedAt = Now,
        CreatedAt = Now,
    };

    private static VendorLinks CreateVendorLinks() => new(
        Options.Create(new StripeOptions()),
        Options.Create(new MetaOptions()),
        Options.Create(new GhlOptions()),
        new ConfigurationBuilder().Build());

    public void Dispose() => _db.Dispose();
}
