using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using RD.Domain;
using RD.Domain.Entities;
using RD.Infrastructure;
using RD.Infrastructure.Enforcement;
using RD.Infrastructure.Gateways;
using RD.Tests.Integration.TestInfra;
using RD.Web.Services;

namespace RD.Tests.Integration;

public sealed class StripeCustomerPreferenceServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 7, 20, 0, 0, TimeSpan.Zero);
    private readonly SyncTestDb _db = new();
    private readonly VendorLinks _vendorLinks;
    private readonly IOptions<StripeOptions> _stripe;
    private readonly MappingWizardService _service;

    public StripeCustomerPreferenceServiceTests()
    {
        _stripe = Options.Create(new StripeOptions
        {
            ApiKey = "rk_test_dummy",
            LedgerLookbackDays = 30,
        });
        _vendorLinks = new VendorLinks(
            _stripe,
            Options.Create(new MetaOptions()),
            Options.Create(new GhlOptions()),
            new ConfigurationBuilder().Build());
        _service = CreateService(_db.Factory);
    }

    [Fact]
    public async Task AddOrReplaceLink_keeps_every_existing_Stripe_customer_and_subscription_active()
    {
        var seeded = SeedClient(("cus_a", "active"), ("cus_b", "canceled"));

        var customer = await _service.AddOrReplaceLink(
            seeded.ClientId, ExternalSystem.Stripe, LinkKind.Customer, "cus_c", "operator");
        var subscription = await _service.AddOrReplaceLink(
            seeded.ClientId, ExternalSystem.Stripe, LinkKind.Subscription, "sub_cus_c", "operator");

        customer.Ok.Should().BeTrue();
        subscription.Ok.Should().BeTrue();

        await using var db = _db.CreateContext();
        var stripeLinks = await db.IdentityLinks
            .Where(l => l.ClientId == seeded.ClientId && l.System == ExternalSystem.Stripe)
            .ToListAsync();
        stripeLinks.Should().OnlyContain(l => l.InvalidatedAt == null);
        stripeLinks.Where(l => l.Kind == LinkKind.Customer).Select(l => l.ExternalId)
            .Should().BeEquivalentTo("cus_a", "cus_b", "cus_c");
        stripeLinks.Where(l => l.Kind == LinkKind.Subscription).Select(l => l.ExternalId)
            .Should().BeEquivalentTo("sub_cus_a", "sub_cus_b", "sub_cus_c");
    }

    [Fact]
    public async Task Required_link_change_supersedes_actions_staged_against_the_previous_mapping()
    {
        var seeded = SeedClient(
            ("cus_a", "active"),
            ("cus_b", "canceled"),
            mode: EnforcementMode.Auto,
            withVerification: true);
        var actionId = Guid.NewGuid();
        await using (var seed = _db.CreateContext())
        {
            seed.OutboxActions.Add(new OutboxAction
            {
                Id = actionId,
                ClientId = seeded.ClientId,
                DecisionId = Guid.Empty,
                ActionType = OutboxActionType.PauseCampaign,
                PayloadJson = "{}",
                IdempotencyKey = $"mapping-change:{actionId:N}",
                Status = OutboxStatus.Approved,
                ExpectedKillSwitchEpoch = 0,
                CreatedAt = Now.AddMinutes(-10),
            });
            await seed.SaveChangesAsync();
        }

        var write = await _service.AddOrReplaceLink(
            seeded.ClientId,
            ExternalSystem.Stripe,
            LinkKind.Customer,
            "cus_new_mapping",
            "operator");

        write.Ok.Should().BeTrue();
        await using var verify = _db.CreateContext();
        var client = await verify.Clients.FindAsync(seeded.ClientId);
        client!.EnforcementMode.Should().Be(EnforcementMode.Shadow);
        var action = await verify.OutboxActions.FindAsync(actionId);
        action!.Status.Should().Be(OutboxStatus.Superseded);
        action.ActionVersion.Should().Be(2);
        action.LastError.Should().Contain("identity mapping changed");
    }

    [Fact]
    public async Task Generic_reconciliation_writer_cannot_close_a_Stripe_customer_ownership_item()
    {
        var seeded = SeedClient(("cus_a", "active"), ("cus_b", "canceled"));
        var genericWriter = new ReconciliationService(_db.Factory, new TestClock(Now));

        var resolved = await genericWriter.ResolveAsync(
            seeded.InvestigationId, "close without ownership proof", dismiss: false);

        resolved.Should().BeFalse();
        await using var db = _db.CreateContext();
        (await db.InvestigationItems.FindAsync(seeded.InvestigationId))!.Status
            .Should().Be(InvestigationStatus.Open);
    }

    [Fact]
    public async Task VerifyMapping_pins_every_active_required_link_when_required_kinds_have_multiple_accounts()
    {
        var clientId = Guid.NewGuid();
        using (var db = _db.CreateContext())
        {
            db.Clients.Add(new Client
            {
                Id = clientId,
                BusinessName = "Multi-account verification",
                ContractType = ContractType.Paid,
                AccountType = AccountType.Master,
                CreatedAt = Now,
            });
            db.IdentityLinks.AddRange(
                Link(clientId, ExternalSystem.Stripe, LinkKind.Customer, "cus_a"),
                Link(clientId, ExternalSystem.Stripe, LinkKind.Customer, "cus_b"),
                Link(clientId, ExternalSystem.Stripe, LinkKind.Subscription, "sub_a"),
                Link(clientId, ExternalSystem.Stripe, LinkKind.Subscription, "sub_b"),
                Link(clientId, ExternalSystem.Meta, LinkKind.Campaign, "campaign_a"),
                Link(clientId, ExternalSystem.Meta, LinkKind.Campaign, "campaign_b"),
                Link(clientId, ExternalSystem.Ghl, LinkKind.Contact, "contact_a"),
                Link(clientId, ExternalSystem.Meta, LinkKind.AdAccount, "act_not_required"));
            db.SaveChanges();
        }

        var result = await _service.VerifyMapping(clientId, "All accounts confirmed.", "operator", true);

        result.Ok.Should().BeTrue();
        await using (var db = _db.CreateContext())
        {
            var activeLinks = await db.IdentityLinks.Where(l => l.ClientId == clientId).ToListAsync();
            var required = activeLinks
                .Where(l => RequiredLinks.IsRequired(l.System, l.Kind))
                .ToList();
            required.Should().HaveCount(7);
            required.Should().OnlyContain(l => l.VerifiedAt == Now);

            var verification = await db.MappingVerifications.SingleAsync(v => v.ClientId == clientId);
            using var json = System.Text.Json.JsonDocument.Parse(verification.VerifiedLinksJson);
            var pinnedIds = json.RootElement.EnumerateArray()
                .Select(element => element.GetProperty("linkId").GetGuid())
                .ToList();
            pinnedIds.Should().BeEquivalentTo(required.Select(l => l.Id));
            pinnedIds.Should().NotContain(activeLinks.Single(l => l.ExternalId == "act_not_required").Id);
        }
    }

    [Fact]
    public async Task Recommended_choice_can_be_overridden_then_changed_later_without_disabling_any_links()
    {
        var seeded = SeedClient(
            ("cus_recommended", "active"),
            ("cus_other", "canceled"),
            mode: EnforcementMode.Auto,
            withVerification: true);

        var choice = await _service.GetStripeCustomerChoice(seeded.ClientId);

        choice.RecommendedExternalId.Should().Be("cus_recommended");
        choice.CurrentPreferredExternalId.Should().BeNull();

        var invalid = await _service.ResolveDuplicateStripe(
            seeded.InvestigationId, "cus_not_linked", choice.CustomerLinks, "reviewer@example.com");
        invalid.Ok.Should().BeFalse();

        // The operator deliberately overrides the recommendation.
        var resolved = await _service.ResolveDuplicateStripe(
            seeded.InvestigationId, "cus_other", choice.CustomerLinks, "reviewer@example.com");
        resolved.Ok.Should().BeTrue();

        await using (var db = _db.CreateContext())
        {
            var client = await db.Clients.SingleAsync(c => c.Id == seeded.ClientId);
            client.PreferredStripeCustomerId.Should().Be("cus_other");
            client.EnforcementMode.Should().Be(EnforcementMode.Auto);

            (await db.IdentityLinks.Where(l => l.ClientId == seeded.ClientId).ToListAsync())
                .Should().OnlyContain(l => l.InvalidatedAt == null);
            (await db.MappingVerifications.SingleAsync(v => v.ClientId == seeded.ClientId))
                .InvalidatedAt.Should().BeNull();

            var audit = await db.StripeCustomerPreferenceChanges.SingleAsync();
            audit.PreviousStripeCustomerId.Should().BeNull();
            audit.PreferredStripeCustomerId.Should().Be("cus_other");
            audit.ChangedBy.Should().Be("reviewer@example.com");

            var item = await db.InvestigationItems.SingleAsync(i => i.Id == seeded.InvestigationId);
            item.Status.Should().Be(InvestigationStatus.Resolved);
            item.ResolutionNote.Should().Contain("All 2 linked Stripe customer account(s) remain active and monitored");
        }

        var changed = await _service.ChangePreferredStripeCustomer(
            seeded.ClientId, "cus_recommended", "owner@example.com");
        changed.Ok.Should().BeTrue();

        await using (var db = _db.CreateContext())
        {
            (await db.Clients.FindAsync(seeded.ClientId))!.PreferredStripeCustomerId
                .Should().Be("cus_recommended");
            var history = await db.StripeCustomerPreferenceChanges.ToListAsync();
            history.Should().HaveCount(2);
            history.Should().ContainSingle(c => c.PreviousStripeCustomerId == "cus_other"
                                                && c.PreferredStripeCustomerId == "cus_recommended"
                                                && c.ChangedBy == "owner@example.com");
            (await db.IdentityLinks.Where(l => l.ClientId == seeded.ClientId).ToListAsync())
                .Should().OnlyContain(l => l.InvalidatedAt == null);
        }
    }

    [Fact]
    public async Task Bulk_action_sets_only_fresh_unambiguous_preferences_and_leaves_ownership_items_open()
    {
        var safe = SeedClient(("cus_safe", "active"), ("cus_terminal", "canceled"));
        var ambiguous = SeedClient(("cus_live_a", "active"), ("cus_live_b", "trialing"));

        var result = await _service.ApplySafeStripeCustomerPreferences("bulk-operator");

        result.Ok.Should().BeTrue();
        result.Applied.Should().Be(1);
        result.AlreadyPreferred.Should().Be(0);
        result.NeedsReview.Should().Be(1);

        await using var db = _db.CreateContext();
        (await db.Clients.FindAsync(safe.ClientId))!.PreferredStripeCustomerId.Should().Be("cus_safe");
        (await db.Clients.FindAsync(ambiguous.ClientId))!.PreferredStripeCustomerId.Should().BeNull();
        (await db.InvestigationItems.FindAsync(safe.InvestigationId))!.Status.Should().Be(InvestigationStatus.Open);
        (await db.InvestigationItems.FindAsync(ambiguous.InvestigationId))!.Status.Should().Be(InvestigationStatus.Open);
        (await db.IdentityLinks.ToListAsync()).Should().OnlyContain(l => l.InvalidatedAt == null);
        (await db.StripeCustomerPreferenceChanges.SingleAsync()).ChangedBy.Should().Be("bulk-operator");
    }

    [Fact]
    public async Task Bulk_action_cannot_cross_an_in_flight_billing_mutation_fence()
    {
        var seeded = SeedClient(("cus_safe", "active"), ("cus_terminal", "canceled"));
        await using var billingDb = _db.CreateContext();
        var billingFence = await ClientMutationFence.AcquireAsync(billingDb, seeded.ClientId);
        Task<StripeCustomerBulkResult>? bulkTask = null;

        try
        {
            bulkTask = _service.ApplySafeStripeCustomerPreferences("bulk-operator");

            var first = await Task.WhenAny(bulkTask, Task.Delay(TimeSpan.FromMilliseconds(200)));
            first.Should().NotBe(
                bulkTask,
                "the bulk preference write must wait while billing holds the client's mutation fence");

            await using var check = _db.CreateContext();
            (await check.Clients.FindAsync(seeded.ClientId))!.PreferredStripeCustomerId.Should().BeNull();
            (await check.StripeCustomerPreferenceChanges.CountAsync()).Should().Be(0);
        }
        finally
        {
            await billingFence.DisposeAsync();
        }

        var result = await bulkTask!;
        result.Ok.Should().BeTrue();
        result.Applied.Should().Be(1);

        await using var verify = _db.CreateContext();
        (await verify.Clients.FindAsync(seeded.ClientId))!.PreferredStripeCustomerId.Should().Be("cus_safe");
    }

    [Fact]
    public async Task Concurrent_preference_changes_are_serialized_and_both_are_audited()
    {
        var seeded = SeedClient(("cus_a", "active"), ("cus_b", "canceled"));
        var service = CreateService(_db.Factory);

        var results = await Task.WhenAll(
            service.ChangePreferredStripeCustomer(seeded.ClientId, "cus_a", "operator-a"),
            service.ChangePreferredStripeCustomer(seeded.ClientId, "cus_b", "operator-b"));

        results.Should().OnlyContain(result => result.Ok);

        await using var db = _db.CreateContext();
        (await db.Clients.FindAsync(seeded.ClientId))!.PreferredStripeCustomerId
            .Should().BeOneOf("cus_a", "cus_b");
        (await db.StripeCustomerPreferenceChanges.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Duplicate_resolution_refuses_a_customer_link_added_after_the_dialog_was_loaded()
    {
        var seeded = SeedClient(("cus_a", "active"), ("cus_b", "canceled"));
        var displayedChoice = await _service.GetStripeCustomerChoice(seeded.ClientId);

        var added = await _service.AddOrReplaceLink(
            seeded.ClientId, ExternalSystem.Stripe, LinkKind.Customer, "cus_unseen", "other-operator");
        added.Ok.Should().BeTrue();

        var result = await _service.ResolveDuplicateStripe(
            seeded.InvestigationId, "cus_a", displayedChoice.CustomerLinks, "reviewer");

        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("changed after you opened");
        await using var db = _db.CreateContext();
        (await db.InvestigationItems.FindAsync(seeded.InvestigationId))!.Status
            .Should().Be(InvestigationStatus.Open);
        (await db.Clients.FindAsync(seeded.ClientId))!.PreferredStripeCustomerId.Should().BeNull();
        (await db.StripeCustomerPreferenceChanges.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Adding_a_customer_after_resolution_opens_a_new_ownership_review()
    {
        var seeded = SeedClient(("cus_a", "active"), ("cus_b", "canceled"));
        var choice = await _service.GetStripeCustomerChoice(seeded.ClientId);
        (await _service.ResolveDuplicateStripe(
            seeded.InvestigationId, "cus_a", choice.CustomerLinks, "reviewer")).Ok.Should().BeTrue();

        var added = await _service.AddOrReplaceLink(
            seeded.ClientId, ExternalSystem.Stripe, LinkKind.Customer, "cus_added_later", "operator");

        added.Ok.Should().BeTrue();
        await using var db = _db.CreateContext();
        var reviews = await db.InvestigationItems
            .Where(i => i.ClientId == seeded.ClientId
                        && i.Kind == InvestigationKind.DuplicateStripeCustomer)
            .OrderBy(i => i.CreatedAt)
            .ToListAsync();
        reviews.Should().HaveCount(2);
        reviews.Should().ContainSingle(i => i.Status == InvestigationStatus.Resolved);
        reviews.Should().ContainSingle(i => i.Status == InvestigationStatus.Open
                                            && i.ExternalId == "cus_added_later");
    }

    [Fact]
    public async Task Concurrent_duplicate_resolutions_return_a_changed_elsewhere_result_instead_of_throwing()
    {
        var seeded = SeedClient(("cus_a", "active"), ("cus_b", "canceled"));
        await using (var seed = _db.CreateContext())
        {
            (await seed.Clients.FindAsync(seeded.ClientId))!.PreferredStripeCustomerId = "cus_a";
            await seed.SaveChangesAsync();
        }
        var service = CreateService(_db.Factory);
        var choice = await _service.GetStripeCustomerChoice(seeded.ClientId);

        var results = await Task.WhenAll(
            service.ResolveDuplicateStripe(seeded.InvestigationId, "cus_a", choice.CustomerLinks, "operator-a"),
            service.ResolveDuplicateStripe(seeded.InvestigationId, "cus_a", choice.CustomerLinks, "operator-b"));

        results.Should().ContainSingle(result => result.Ok);
        results.Should().ContainSingle(result => !result.Ok
                                                  && result.Message.Contains("already handled", StringComparison.OrdinalIgnoreCase));

        await using var db = _db.CreateContext();
        (await db.InvestigationItems.FindAsync(seeded.InvestigationId))!.Status
            .Should().Be(InvestigationStatus.Resolved);
        (await db.StripeCustomerPreferenceChanges.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Concurrent_bulk_preference_runs_are_serialized_without_duplicate_history()
    {
        var seeded = SeedClient(("cus_a", "active"), ("cus_b", "canceled"));
        var service = CreateService(_db.Factory);

        var results = await Task.WhenAll(
            service.ApplySafeStripeCustomerPreferences("operator-a"),
            service.ApplySafeStripeCustomerPreferences("operator-b"));

        results.Should().OnlyContain(result => result.Ok);
        results.Sum(result => result.Applied).Should().Be(1);
        results.Sum(result => result.AlreadyPreferred).Should().Be(1);

        await using var db = _db.CreateContext();
        (await db.Clients.FindAsync(seeded.ClientId))!.PreferredStripeCustomerId.Should().Be("cus_a");
        (await db.StripeCustomerPreferenceChanges.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Preference_history_rejects_updates_and_deletes()
    {
        var seeded = SeedClient(("cus_a", "active"), ("cus_b", "canceled"));
        (await _service.ChangePreferredStripeCustomer(seeded.ClientId, "cus_a", "operator")).Ok.Should().BeTrue();

        await using (var update = _db.CreateContext())
        {
            var audit = await update.StripeCustomerPreferenceChanges.SingleAsync();
            audit.Reason = "rewrite history";
            var act = async () => await update.SaveChangesAsync();
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*immutable*");
        }

        await using (var delete = _db.CreateContext())
        {
            var audit = await delete.StripeCustomerPreferenceChanges.SingleAsync();
            delete.Remove(audit);
            var act = async () => await delete.SaveChangesAsync();
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*append-only*");
        }
    }

    [Fact]
    public async Task Bulk_action_changes_nothing_when_the_latest_completed_sync_is_stale()
    {
        var seeded = SeedClient(("cus_safe", "active"), ("cus_terminal", "canceled"),
            completedSyncAt: Now.AddMinutes(-31));

        var result = await _service.ApplySafeStripeCustomerPreferences("bulk-operator");

        result.Ok.Should().BeFalse();
        result.Applied.Should().Be(0);
        await using var db = _db.CreateContext();
        (await db.Clients.FindAsync(seeded.ClientId))!.PreferredStripeCustomerId.Should().BeNull();
        (await db.InvestigationItems.FindAsync(seeded.InvestigationId))!.Status.Should().Be(InvestigationStatus.Open);
        (await db.StripeCustomerPreferenceChanges.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Choice_abstains_cleanly_when_no_completed_sync_exists()
    {
        var seeded = SeedClient(("cus_a", "active"), ("cus_b", "canceled"), addCompletedSync: false);

        var choice = await _service.GetStripeCustomerChoice(seeded.ClientId);

        choice.RecommendedExternalId.Should().BeNull();
        choice.RecommendationReason.Should().Contain("no completed Stripe sync");
    }

    [Fact]
    public async Task Fresh_completed_run_does_not_make_stale_projection_rows_trustworthy()
    {
        var seeded = SeedClient(("cus_a", "active"), ("cus_b", "canceled"));
        using (var db = _db.CreateContext())
        {
            foreach (var subscription in await db.StripeSubscriptions.ToListAsync())
                subscription.SourceSyncedAt = Now.AddDays(-1);
            await db.SaveChangesAsync();
        }

        var choice = await _service.GetStripeCustomerChoice(seeded.ClientId);

        choice.RecommendedExternalId.Should().BeNull();
        choice.RecommendationReason.Should().Contain("not enough current billing evidence");
    }

    [Fact]
    public async Task Paid_invoice_fallback_uses_only_successful_recent_subscription_invoices()
    {
        var seeded = SeedClient(("cus_a", "canceled"), ("cus_b", "canceled"));
        using (var db = _db.CreateContext())
        {
            db.StripeInvoices.AddRange(
                Invoice("in_valid", "cus_b", "paid", 100m, "sub_cus_b", Now.AddDays(-2)),
                Invoice("in_open", "cus_a", "open", 100m, "sub_cus_a", Now.AddDays(-1)),
                Invoice("in_zero", "cus_a", "paid", 0m, "sub_cus_a", Now.AddDays(-1)),
                Invoice("in_oneoff", "cus_a", "paid", 100m, null, Now.AddDays(-1)),
                Invoice("in_no_paid_at", "cus_a", "paid", 100m, "sub_cus_a", null),
                Invoice("in_too_old", "cus_a", "paid", 100m, "sub_cus_a", Now.AddDays(-31)));
            db.SaveChanges();
        }

        var choice = await _service.GetStripeCustomerChoice(seeded.ClientId);

        choice.RecommendedExternalId.Should().Be("cus_b");
        choice.RecommendationReason.Should().Contain("successful recent subscription payment");
        choice.CanAutoApply.Should().BeFalse("paid-invoice-only evidence is a manual suggestion");
    }

    [Fact]
    public async Task Known_old_invoice_paid_recently_prevents_a_conflicting_paid_owner_recommendation()
    {
        var seeded = SeedClient(("cus_a", "canceled"), ("cus_b", "canceled"));
        using (var db = _db.CreateContext())
        {
            db.StripeInvoices.AddRange(
                Invoice("in_b", "cus_b", "paid", 100m, "sub_cus_b", Now.AddDays(-2)),
                Invoice("in_old_created", "cus_a", "paid", 100m, "sub_cus_a", Now.AddDays(-1), Now.AddDays(-60)));
            db.SaveChanges();
        }

        var choice = await _service.GetStripeCustomerChoice(seeded.ClientId);

        choice.RecommendedExternalId.Should().BeNull();
        choice.RecommendationReason.Should().Contain("more than one account received a recent subscription payment");
    }

    private SeededClient SeedClient(
        (string CustomerId, string Status) first,
        (string CustomerId, string Status) second,
        EnforcementMode mode = EnforcementMode.Shadow,
        bool withVerification = false,
        DateTimeOffset? completedSyncAt = null,
        bool addCompletedSync = true)
    {
        using var db = _db.CreateContext();
        var client = new Client
        {
            Id = Guid.NewGuid(),
            BusinessName = "Preference Test " + Guid.NewGuid().ToString("N")[..6],
            ContractType = ContractType.Paid,
            AccountType = AccountType.Own,
            EnforcementMode = mode,
            CreatedAt = Now,
        };
        db.Clients.Add(client);

        var customerLinks = new[] { first.CustomerId, second.CustomerId }.Select(customerId => new IdentityLink
        {
            Id = Guid.NewGuid(), ClientId = client.Id, System = ExternalSystem.Stripe,
            Kind = LinkKind.Customer, ExternalId = customerId, VerifiedAt = Now, CreatedAt = Now,
        }).ToList();
        db.IdentityLinks.AddRange(customerLinks);
        var subscriptionLinks = new[] { first.CustomerId, second.CustomerId }.Select(customerId => new IdentityLink
        {
            Id = Guid.NewGuid(), ClientId = client.Id, System = ExternalSystem.Stripe,
            Kind = LinkKind.Subscription, ExternalId = "sub_" + customerId, VerifiedAt = Now, CreatedAt = Now,
        }).ToList();
        db.IdentityLinks.AddRange(subscriptionLinks);
        db.StripeSubscriptions.AddRange(
            Subscription(first.CustomerId, first.Status),
            Subscription(second.CustomerId, second.Status));

        var investigation = new InvestigationItem
        {
            Id = Guid.NewGuid(), ClientId = client.Id,
            Kind = InvestigationKind.DuplicateStripeCustomer,
            Status = InvestigationStatus.Open,
            Detail = "Confirm this multi-customer cluster.",
            CreatedAt = Now,
        };
        db.InvestigationItems.Add(investigation);

        if (withVerification)
        {
            db.MappingVerifications.Add(new MappingVerification
            {
                Id = Guid.NewGuid(), ClientId = client.Id,
                VerifiedLinksJson = System.Text.Json.JsonSerializer.Serialize(
                    customerLinks.Concat(subscriptionLinks)
                        .Select(link => new { linkId = link.Id, linkVersion = link.LinkVersion })),
                VerifiedBy = "prior-reviewer",
                BlastRadiusAcknowledged = true, VerifiedAt = Now,
            });
        }

        if (addCompletedSync)
        {
            db.SyncRuns.Add(new SyncRun
            {
                Id = Guid.NewGuid(), System = ExternalSystem.Stripe,
                Status = SyncRunStatus.Completed,
                StartedAt = (completedSyncAt ?? Now.AddMinutes(-5)).AddMinutes(-1),
                CompletedAt = completedSyncAt ?? Now.AddMinutes(-5),
            });
        }
        db.SaveChanges();
        return new SeededClient(client.Id, investigation.Id);
    }

    private static StripeSubscriptionProj Subscription(string customerId, string status) => new()
    {
        SubscriptionId = "sub_" + customerId,
        CustomerId = customerId,
        Status = status,
        SourceSyncedAt = Now,
    };

    private MappingWizardService CreateService(IDbContextFactory<RdDbContext> factory)
        => new(factory, new TestClock(Now), _vendorLinks, _stripe);

    private static IdentityLink Link(Guid clientId, ExternalSystem system, LinkKind kind, string externalId) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        System = system,
        Kind = kind,
        ExternalId = externalId,
        CreatedAt = Now,
    };

    private static StripeInvoiceProj Invoice(
        string invoiceId,
        string customerId,
        string status,
        decimal amountPaid,
        string? subscriptionId,
        DateTimeOffset? paidAt,
        DateTimeOffset? createdAt = null) => new()
    {
        InvoiceId = invoiceId,
        CustomerId = customerId,
        SubscriptionId = subscriptionId,
        Status = status,
        AmountPaid = amountPaid,
        CreatedAtSource = createdAt ?? paidAt ?? Now,
        PaidAt = paidAt,
        SourceSyncedAt = Now,
    };

    public void Dispose() => _db.Dispose();

    private sealed record SeededClient(Guid ClientId, Guid InvestigationId);

}
