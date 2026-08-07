using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using RD.Domain;
using RD.Domain.Entities;
using RD.Infrastructure.Enforcement;
using RD.Infrastructure.Gateways;
using RD.Tests.Integration.TestInfra;
using RD.Web.Services;

namespace RD.Tests.Integration;

public sealed class RetiredClientMappingMutationTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 9, 0, 0, TimeSpan.Zero);
    private readonly SyncTestDb _db = new();
    private readonly MappingWizardService _service;

    public RetiredClientMappingMutationTests()
    {
        var stripe = Options.Create(new StripeOptions
        {
            ApiKey = "rk_test_dummy",
            LedgerLookbackDays = 30,
        });
        var vendorLinks = new VendorLinks(
            stripe,
            Options.Create(new MetaOptions()),
            Options.Create(new GhlOptions()),
            new ConfigurationBuilder().Build());
        _service = new MappingWizardService(_db.Factory, new TestClock(Now), vendorLinks, stripe);
    }

    [Fact]
    public async Task GetUnverifiedClients_excludes_retired_master_clients()
    {
        var liveId = Guid.NewGuid();
        var survivorId = Guid.NewGuid();
        var retiredId = Guid.NewGuid();
        await using (var seed = _db.CreateContext())
        {
            seed.Clients.AddRange(
                Client(liveId, "Live master", AccountType.Master),
                Client(survivorId, "Survivor", AccountType.Own),
                Client(retiredId, "Retired master", AccountType.Master, survivorId));
            await seed.SaveChangesAsync();
        }

        var rows = await _service.GetUnverifiedClients();

        rows.Select(row => row.Id).Should().Contain(liveId);
        rows.Select(row => row.Id).Should().NotContain(retiredId);
    }

    public static TheoryData<RetiredMappingMutation> RetiredMappingMutations => new()
    {
        RetiredMappingMutation.AddOrReplaceLink,
        RetiredMappingMutation.VerifyMapping,
        RetiredMappingMutation.PromoteToAssist,
        RetiredMappingMutation.ResolveDuplicateStripe,
        RetiredMappingMutation.ChangePreferredStripeCustomer,
    };

    [Theory]
    [MemberData(nameof(RetiredMappingMutations))]
    public async Task Mapping_mutation_entry_points_reject_retired_clients_without_writes(
        RetiredMappingMutation mutation)
    {
        var seeded = await SeedRetiredMappedClientAsync();
        var before = await ReadStateAsync(seeded.ClientId, seeded.InvestigationId);

        var result = mutation switch
        {
            RetiredMappingMutation.AddOrReplaceLink => await _service.AddOrReplaceLink(
                seeded.ClientId, ExternalSystem.Stripe, LinkKind.Customer, "cus_new", "operator"),
            RetiredMappingMutation.VerifyMapping => await _service.VerifyMapping(
                seeded.ClientId, "confirmed", "operator", blastRadiusAcknowledged: true),
            RetiredMappingMutation.PromoteToAssist => await _service.PromoteToAssist(seeded.ClientId),
            RetiredMappingMutation.ResolveDuplicateStripe => await _service.ResolveDuplicateStripe(
                seeded.InvestigationId, "cus_primary", seeded.CustomerLinks, "operator"),
            RetiredMappingMutation.ChangePreferredStripeCustomer => await _service.ChangePreferredStripeCustomer(
                seeded.ClientId, "cus_primary", "operator"),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null),
        };

        result.Ok.Should().BeFalse();
        result.Message.Should().ContainEquivalentOf("merged");
        (await ReadStateAsync(seeded.ClientId, seeded.InvestigationId)).Should().Be(before);
    }

    [Fact]
    public async Task Bulk_preference_mutation_fails_without_writes_when_queue_contains_a_retired_client()
    {
        var seeded = await SeedRetiredMappedClientAsync();
        var before = await ReadStateAsync(seeded.ClientId, seeded.InvestigationId);

        var result = await _service.ApplySafeStripeCustomerPreferences("operator");

        result.Ok.Should().BeFalse();
        result.Message.Should().ContainEquivalentOf("merged");
        (await ReadStateAsync(seeded.ClientId, seeded.InvestigationId)).Should().Be(before);
    }

    [Fact]
    public async Task AddOrReplaceLink_reloads_after_the_fence_and_rejects_a_client_retired_while_waiting()
    {
        var survivorId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        await using (var seed = _db.CreateContext())
        {
            seed.Clients.AddRange(
                Client(survivorId, "Survivor", AccountType.Own),
                Client(clientId, "About to retire", AccountType.Master));
            await seed.SaveChangesAsync();
        }

        await using var mergeDb = _db.CreateContext();
        var mergeFence = await ClientMutationFence.AcquireAsync(mergeDb, clientId);
        Task<MappingWriteResult>? mappingTask = null;
        try
        {
            mappingTask = _service.AddOrReplaceLink(
                clientId, ExternalSystem.Stripe, LinkKind.Customer, "cus_after_merge", "operator");
            var first = await Task.WhenAny(mappingTask, Task.Delay(TimeSpan.FromMilliseconds(200)));
            first.Should().NotBe(mappingTask, "mapping must wait for a merge holding the client fence");

            var client = await mergeDb.Clients.SingleAsync(c => c.Id == clientId);
            client.MergedIntoClientId = survivorId;
            client.MergedAt = Now;
            await mergeDb.SaveChangesAsync();
        }
        finally
        {
            await mergeFence.DisposeAsync();
        }

        var result = await mappingTask!;
        result.Ok.Should().BeFalse();
        result.Message.Should().ContainEquivalentOf("merged");

        await using var verify = _db.CreateContext();
        (await verify.IdentityLinks.CountAsync(l => l.ClientId == clientId)).Should().Be(0);
    }

    private async Task<SeededRetiredClient> SeedRetiredMappedClientAsync()
    {
        var survivorId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var investigationId = Guid.NewGuid();
        var requiredLinks = new List<IdentityLink>
        {
            Link(clientId, ExternalSystem.Stripe, LinkKind.Customer, "cus_primary"),
            Link(clientId, ExternalSystem.Stripe, LinkKind.Customer, "cus_secondary"),
            Link(clientId, ExternalSystem.Stripe, LinkKind.Subscription, "sub_primary"),
            Link(clientId, ExternalSystem.Stripe, LinkKind.Subscription, "sub_secondary"),
            Link(clientId, ExternalSystem.Meta, LinkKind.Campaign, "campaign_primary"),
            Link(clientId, ExternalSystem.Ghl, LinkKind.Contact, "contact_primary"),
        };

        await using var seed = _db.CreateContext();
        seed.Clients.AddRange(
            Client(survivorId, "Survivor", AccountType.Own),
            Client(clientId, "Retired duplicate", AccountType.Master, survivorId));
        seed.IdentityLinks.AddRange(requiredLinks);
        seed.MappingVerifications.Add(new MappingVerification
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            VerifiedLinksJson = JsonSerializer.Serialize(
                requiredLinks.Select(link => new { linkId = link.Id, linkVersion = link.LinkVersion })),
            VerifiedBy = "prior-reviewer",
            BlastRadiusAcknowledged = true,
            VerifiedAt = Now,
        });
        seed.InvestigationItems.Add(new InvestigationItem
        {
            Id = investigationId,
            ClientId = clientId,
            Kind = InvestigationKind.DuplicateStripeCustomer,
            Status = InvestigationStatus.Open,
            Detail = "Confirm ownership.",
            CreatedAt = Now,
        });
        seed.StripeSubscriptions.AddRange(
            Subscription("sub_primary", "cus_primary", "active"),
            Subscription("sub_secondary", "cus_secondary", "canceled"));
        seed.SyncRuns.Add(new SyncRun
        {
            Id = Guid.NewGuid(),
            System = ExternalSystem.Stripe,
            Status = SyncRunStatus.Completed,
            StartedAt = Now.AddMinutes(-6),
            CompletedAt = Now.AddMinutes(-5),
        });
        await seed.SaveChangesAsync();

        return new SeededRetiredClient(
            clientId,
            investigationId,
            requiredLinks
                .Where(link => link.System == ExternalSystem.Stripe && link.Kind == LinkKind.Customer)
                .Select(link => new StripeCustomerLinkStamp(link.Id, link.ExternalId, link.LinkVersion))
                .ToList());
    }

    private async Task<MappingState> ReadStateAsync(Guid clientId, Guid investigationId)
    {
        await using var db = _db.CreateContext();
        var client = await db.Clients.AsNoTracking().SingleAsync(c => c.Id == clientId);
        return new MappingState(
            await db.IdentityLinks.CountAsync(link => link.ClientId == clientId),
            await db.MappingVerifications.CountAsync(verification => verification.ClientId == clientId),
            await db.StripeCustomerPreferenceChanges.CountAsync(change => change.ClientId == clientId),
            client.EnforcementMode,
            client.PreferredStripeCustomerId,
            (await db.InvestigationItems.AsNoTracking().SingleAsync(item => item.Id == investigationId)).Status);
    }

    private static Client Client(
        Guid id,
        string name,
        AccountType accountType,
        Guid? mergedIntoClientId = null) => new()
    {
        Id = id,
        BusinessName = name,
        ContractType = ContractType.Paid,
        AccountType = accountType,
        EnforcementMode = EnforcementMode.Shadow,
        MergedIntoClientId = mergedIntoClientId,
        MergedAt = mergedIntoClientId is null ? null : Now,
        CreatedAt = Now,
    };

    private static IdentityLink Link(
        Guid clientId,
        ExternalSystem system,
        LinkKind kind,
        string externalId) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        System = system,
        Kind = kind,
        ExternalId = externalId,
        VerifiedAt = Now,
        CreatedAt = Now,
    };

    private static StripeSubscriptionProj Subscription(
        string subscriptionId,
        string customerId,
        string status) => new()
    {
        SubscriptionId = subscriptionId,
        CustomerId = customerId,
        Status = status,
        SourceSyncedAt = Now,
    };

    public void Dispose() => _db.Dispose();

    public enum RetiredMappingMutation
    {
        AddOrReplaceLink,
        VerifyMapping,
        PromoteToAssist,
        ResolveDuplicateStripe,
        ChangePreferredStripeCustomer,
    }

    private sealed record SeededRetiredClient(
        Guid ClientId,
        Guid InvestigationId,
        IReadOnlyCollection<StripeCustomerLinkStamp> CustomerLinks);

    private sealed record MappingState(
        int LinkCount,
        int VerificationCount,
        int PreferenceChangeCount,
        EnforcementMode EnforcementMode,
        string? PreferredStripeCustomerId,
        InvestigationStatus InvestigationStatus);
}
