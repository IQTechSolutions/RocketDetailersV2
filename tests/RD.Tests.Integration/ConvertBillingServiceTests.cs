using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text.Json;
using RD.Domain;
using RD.Domain.Entities;
using RD.Infrastructure.Enforcement;
using RD.Infrastructure.Gateways;
using RD.Tests.Integration.TestInfra;
using RD.Web.Services;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace RD.Tests.Integration;

/// <summary>
/// The money-write (rung B / Assist): ConvertBillingService.ExecuteAsync creates the drafted Stripe
/// subscription behind a human action. Proves it creates the customer + subscription (writing the
/// IdentityLinks), moves the intent to AwaitingPayment, reuses an existing customer, and refuses to
/// bill a not-ready draft or a non-Drafted intent. WireMock stands in for Stripe; a throwaway LocalDB
/// stands in for SQL.
/// </summary>
public sealed class ConvertBillingServiceTests : IDisposable
{
    private readonly SyncTestDb _db = new();
    private readonly WireMockServer _server = WireMockServer.Start();
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        _server.Stop();
        _server.Dispose();
        _db.Dispose();
    }

    private ConvertBillingService Service(
        Func<string, CancellationToken, Task>? beforeStripeWrite = null)
    {
        var options = Options.Create(new StripeOptions { ApiKey = "rk_test_dummy", BaseUrl = _server.Urls[0], ApiVersion = "2025-03-31.basil" });
        var gateway = new StripeGateway(new HttpClient(), options, new RetryHelper { BaseDelay = TimeSpan.FromMilliseconds(1) });
        var killSwitch = new KillSwitchService(_db.Factory, _clock);
        return new ConvertBillingService(
            _db.Factory, gateway, killSwitch, _clock, beforeStripeWrite);
    }

    private (Guid clientId, Guid intentId) Seed(string? priceId, bool withCustomerLink,
        ConvertIntentState state = ConvertIntentState.Drafted, string? subscriptionId = null)
    {
        _server.Given(Request.Create().WithPath("/v1/subscriptions").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBodyAsJson(new { data = Array.Empty<object>(), has_more = false }));

        using var db = _db.CreateContext();
        var pkg = new Package { Id = Guid.NewGuid(), Name = "Pkg-" + Guid.NewGuid().ToString("N")[..6], IsActive = true };
        db.Packages.Add(pkg);
        db.PackageVersions.Add(new PackageVersion
        {
            Id = Guid.NewGuid(), PackageId = pkg.Id, EffectiveFrom = _clock.UtcNow,
            DailyRate = 10m, DailyBudget = 20m, CurrencyCode = "USD", StripePriceId = priceId,
            CreatedBy = "test", CreatedAt = _clock.UtcNow,
        });
        db.SyncRuns.Add(new SyncRun
        {
            Id = Guid.NewGuid(),
            System = ExternalSystem.Stripe,
            Status = SyncRunStatus.Completed,
            StartedAt = _clock.UtcNow.AddMinutes(-2),
            CompletedAt = _clock.UtcNow.AddMinutes(-1),
        });
        var client = new Client
        {
            Id = Guid.NewGuid(), BusinessName = "Own Detailing", ContactName = "Jane", Email = "jane@x.com",
            ContractType = ContractType.Trial, AccountType = AccountType.Own, CurrencyCode = "USD",
            PackageId = pkg.Id, CreatedAt = _clock.UtcNow,
        };
        db.Clients.Add(client);
        if (withCustomerLink)
            db.IdentityLinks.Add(new IdentityLink
            {
                Id = Guid.NewGuid(), ClientId = client.Id, System = ExternalSystem.Stripe,
                Kind = LinkKind.Customer, ExternalId = "cus_existing", VerifiedAt = _clock.UtcNow, CreatedAt = _clock.UtcNow,
            });
        var intent = new ConvertIntent
        {
            Id = Guid.NewGuid(), ClientId = client.Id, AccountType = AccountType.Own, PackageId = pkg.Id,
            State = state, StripeSubscriptionId = subscriptionId, CreatedAt = _clock.UtcNow, UpdatedAt = _clock.UtcNow,
        };
        db.ConvertIntents.Add(intent);
        db.SaveChanges();
        return (client.Id, intent.Id);
    }

    private void StubSubscription(string customer) =>
        _server.Given(Request.Create().WithPath("/v1/subscriptions").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                id = "sub_new", customer, status = "active", currency = "usd",
                items = new { data = new[] { new { price = new { unit_amount = 9900, currency = "usd", recurring = new { interval = "month" } } } } },
            }));

    private DateTimeOffset FreezeForRetry(Guid intentId, string customerId = "cus_existing")
    {
        var startedAt = _clock.UtcNow.AddMinutes(-10);
        using var db = _db.CreateContext();
        var frozen = new ConvertDraft(
            true, "Approved", AccountType.Own, "price_1", customerId, false, "USD", []);
        var intent = db.ConvertIntents.Single(i => i.Id == intentId);
        intent.DraftedActionJson = JsonSerializer.Serialize(frozen);
        intent.StripeCustomerId = customerId;
        intent.BillingStartedAt = startedAt;
        intent.UpdatedAt = _clock.UtcNow.AddMinutes(-6);
        db.SaveChanges();
        return startedAt;
    }

    [Fact]
    public async Task Execute_creates_customer_and_subscription_and_moves_to_awaiting_payment()
    {
        var (clientId, intentId) = Seed(priceId: "price_1", withCustomerLink: false);
        _server.Given(Request.Create().WithPath("/v1/customers").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { id = "cus_new", created = 1_700_000_000 }));
        StubSubscription("cus_new");

        var result = await Service().ExecuteAsync(intentId, "op", "price_1", null, true, CancellationToken.None);

        result.Ok.Should().BeTrue();

        await using var db = _db.CreateContext();
        var intent = db.ConvertIntents.Single(i => i.Id == intentId);
        intent.State.Should().Be(ConvertIntentState.AwaitingPayment);
        intent.StripeCustomerId.Should().Be("cus_new");
        intent.ExpiresAt.Should().NotBeNull();

        var links = db.IdentityLinks.Where(l => l.ClientId == clientId && l.System == ExternalSystem.Stripe).ToList();
        links.Should().Contain(l => l.Kind == LinkKind.Customer && l.ExternalId == "cus_new");
        links.Should().Contain(l => l.Kind == LinkKind.Subscription && l.ExternalId == "sub_new");
    }

    [Fact]
    public async Task Execute_reuses_an_existing_customer()
    {
        var (_, intentId) = Seed(priceId: "price_1", withCustomerLink: true);
        StubSubscription("cus_existing"); // no /v1/customers stub — a create attempt would 404 and throw

        var result = await Service().ExecuteAsync(intentId, "op", "price_1", "cus_existing", false, CancellationToken.None);

        result.Ok.Should().BeTrue();
        _server.LogEntries.Should().NotContain(e => e.RequestMessage.Path.Contains("/v1/customers"));

        await using var db = _db.CreateContext();
        db.ConvertIntents.Single(i => i.Id == intentId).StripeCustomerId.Should().Be("cus_existing");
    }

    [Fact]
    public async Task Execute_reconciles_a_paid_invoice_that_arrived_before_subscription_persistence()
    {
        var (clientId, intentId) = Seed(priceId: "price_1", withCustomerLink: true);
        using (var db = _db.CreateContext())
        {
            db.StripeInvoices.Add(new StripeInvoiceProj
            {
                InvoiceId = "in_fast", ClientId = clientId, CustomerId = "cus_existing",
                SubscriptionId = "sub_new", Status = "paid", AmountDue = 99m, AmountPaid = 99m,
                PaidAt = _clock.UtcNow, CreatedAtSource = _clock.UtcNow, SourceSyncedAt = _clock.UtcNow,
            });
            db.TrialPeriods.Add(new TrialPeriod
            {
                Id = Guid.NewGuid(), ClientId = clientId,
                StartsAt = _clock.UtcNow.AddDays(-2), Outcome = TrialOutcome.Active,
            });
            db.SaveChanges();
        }
        StubSubscription("cus_existing");

        var result = await Service().ExecuteAsync(
            intentId, "op", "price_1", "cus_existing", false, CancellationToken.None);

        result.Ok.Should().BeTrue(result.Message);
        await using var verify = _db.CreateContext();
        (await verify.ConvertIntents.SingleAsync(i => i.Id == intentId)).State
            .Should().Be(ConvertIntentState.Paid);
        (await verify.Clients.SingleAsync(c => c.Id == clientId)).ContractType.Should().Be(ContractType.Paid);
        (await verify.TrialPeriods.SingleAsync(t => t.ClientId == clientId)).Outcome
            .Should().Be(TrialOutcome.Promoted);
    }

    [Fact]
    public async Task Execute_uses_the_explicit_preferred_customer_when_multiple_are_linked()
    {
        var (clientId, intentId) = Seed(priceId: "price_1", withCustomerLink: true);
        using (var db = _db.CreateContext())
        {
            db.IdentityLinks.Add(new IdentityLink
            {
                Id = Guid.NewGuid(), ClientId = clientId, System = ExternalSystem.Stripe,
                Kind = LinkKind.Customer, ExternalId = "cus_preferred", CreatedAt = _clock.UtcNow,
            });
            (await db.Clients.FindAsync(clientId))!.PreferredStripeCustomerId = "cus_preferred";
            await db.SaveChangesAsync();
        }
        StubSubscription("cus_preferred");

        var result = await Service().ExecuteAsync(intentId, "op", "price_1", "cus_preferred", false, CancellationToken.None);

        result.Ok.Should().BeTrue(result.Message);
        _server.LogEntries.Should().NotContain(e => e.RequestMessage.Path.Contains("/v1/customers"));
        await using var verify = _db.CreateContext();
        (await verify.ConvertIntents.SingleAsync(i => i.Id == intentId)).StripeCustomerId
            .Should().Be("cus_preferred");
    }

    [Fact]
    public async Task Execute_refuses_multiple_customers_without_a_preference_instead_of_guessing_or_creating_one()
    {
        var (clientId, intentId) = Seed(priceId: "price_1", withCustomerLink: true);
        using (var db = _db.CreateContext())
        {
            db.IdentityLinks.Add(new IdentityLink
            {
                Id = Guid.NewGuid(), ClientId = clientId, System = ExternalSystem.Stripe,
                Kind = LinkKind.Customer, ExternalId = "cus_second", CreatedAt = _clock.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var result = await Service().ExecuteAsync(intentId, "op", "price_1", null, false, CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("preferred billing customer");
        _server.LogEntries.Should().BeEmpty();
        await using var verify = _db.CreateContext();
        (await verify.ConvertIntents.SingleAsync(i => i.Id == intentId)).State
            .Should().Be(ConvertIntentState.Drafted);
    }

    [Fact]
    public async Task Execute_refuses_when_package_has_no_price_and_calls_no_stripe()
    {
        var (_, intentId) = Seed(priceId: null, withCustomerLink: false);

        var result = await Service().ExecuteAsync(intentId, "op", null, null, true, CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("Stripe price");
        _server.LogEntries.Should().BeEmpty(); // never touched Stripe

        await using var db = _db.CreateContext();
        db.ConvertIntents.Single(i => i.Id == intentId).State.Should().Be(ConvertIntentState.Drafted);
    }

    [Fact]
    public async Task Execute_refuses_when_intent_is_not_drafted()
    {
        var (_, intentId) = Seed(priceId: "price_1", withCustomerLink: true, state: ConvertIntentState.AwaitingPayment);

        var result = await Service().ExecuteAsync(intentId, "op", "price_1", "cus_existing", false, CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("already");
        _server.LogEntries.Should().BeEmpty();
    }

    [Fact]
    public async Task Execute_refuses_stale_or_missing_Stripe_evidence_before_any_vendor_write()
    {
        var (_, intentId) = Seed(priceId: "price_1", withCustomerLink: false);
        using (var db = _db.CreateContext())
        {
            db.SyncRuns.RemoveRange(db.SyncRuns);
            db.SaveChanges();
        }

        var result = await Service().ExecuteAsync(
            intentId, "op", "price_1", null, true, CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("Stripe sync");
        _server.LogEntries.Should().BeEmpty();
        await using var verify = _db.CreateContext();
        (await verify.ConvertIntents.SingleAsync(i => i.Id == intentId)).BillingStartedAt.Should().BeNull();
    }

    [Fact]
    public async Task Execute_refuses_when_the_preference_changed_after_the_operator_saw_the_draft()
    {
        var (clientId, intentId) = Seed(priceId: "price_1", withCustomerLink: true);
        using (var db = _db.CreateContext())
        {
            db.IdentityLinks.Add(new IdentityLink
            {
                Id = Guid.NewGuid(), ClientId = clientId, System = ExternalSystem.Stripe,
                Kind = LinkKind.Customer, ExternalId = "cus_new_target", CreatedAt = _clock.UtcNow,
            });
            db.Clients.Single(c => c.Id == clientId).PreferredStripeCustomerId = "cus_new_target";
            db.SaveChanges();
        }

        var result = await Service().ExecuteAsync(
            intentId, "op", "price_1", "cus_existing", false, CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("changed after it was displayed");
        _server.LogEntries.Should().BeEmpty();
    }

    [Fact]
    public async Task Mapping_change_cannot_cross_the_final_cluster_check_and_Stripe_write()
    {
        var (clientId, intentId) = Seed(priceId: "price_1", withCustomerLink: true);
        await using var mappingDb = _db.CreateContext();
        var mappingFence = await ClientMutationFence.AcquireAsync(mappingDb, clientId);
        Task<ConvertResult>? executeTask = null;
        try
        {
            mappingDb.IdentityLinks.Add(new IdentityLink
            {
                Id = Guid.NewGuid(),
                ClientId = clientId,
                System = ExternalSystem.Stripe,
                Kind = LinkKind.Customer,
                ExternalId = "cus_added_during_billing",
                CreatedAt = _clock.UtcNow,
            });
            mappingDb.InvestigationItems.Add(new InvestigationItem
            {
                Id = Guid.NewGuid(),
                ClientId = clientId,
                Kind = InvestigationKind.DuplicateStripeCustomer,
                System = ExternalSystem.Stripe,
                Status = InvestigationStatus.Open,
                Detail = "Concurrent ownership review.",
                CreatedAt = _clock.UtcNow,
            });
            await mappingDb.SaveChangesAsync();

            executeTask = Service().ExecuteAsync(
                intentId,
                "op",
                "price_1",
                "cus_existing",
                expectedWouldCreateCustomer: false,
                CancellationToken.None);

            var first = await Task.WhenAny(executeTask, Task.Delay(TimeSpan.FromMilliseconds(200)));
            first.Should().NotBe(executeTask, "billing must wait for the in-flight mapping mutation fence");
        }
        finally
        {
            await mappingFence.DisposeAsync();
        }

        var result = await executeTask!;

        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("Can't bill yet");
        _server.LogEntries.Where(entry => entry.RequestMessage.Method == "POST").Should().BeEmpty();
    }

    [Theory]
    [InlineData(InvestigationStatus.Open)]
    [InlineData(InvestigationStatus.Dismissed)]
    public async Task Execute_refuses_any_unresolved_multiple_customer_ownership_investigation(
        InvestigationStatus investigationStatus)
    {
        var (clientId, intentId) = Seed(priceId: "price_1", withCustomerLink: true);
        using (var db = _db.CreateContext())
        {
            db.InvestigationItems.Add(new InvestigationItem
            {
                Id = Guid.NewGuid(), ClientId = clientId,
                Kind = InvestigationKind.DuplicateStripeCustomer,
                Status = investigationStatus,
                Detail = "Confirm or split this Stripe customer cluster.",
                CreatedAt = _clock.UtcNow,
            });
            db.SaveChanges();
        }

        var result = await Service().ExecuteAsync(
            intentId, "op", "price_1", "cus_existing", false, CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("ownership is still unconfirmed");
        _server.LogEntries.Should().BeEmpty();
    }

    [Fact]
    public async Task Execute_refuses_when_any_linked_customer_has_a_current_subscription()
    {
        var (clientId, intentId) = Seed(priceId: "price_1", withCustomerLink: true);
        using (var db = _db.CreateContext())
        {
            db.IdentityLinks.Add(new IdentityLink
            {
                Id = Guid.NewGuid(), ClientId = clientId, System = ExternalSystem.Stripe,
                Kind = LinkKind.Customer, ExternalId = "cus_preferred", CreatedAt = _clock.UtcNow,
            });
            db.Clients.Single(c => c.Id == clientId).PreferredStripeCustomerId = "cus_preferred";
            db.StripeSubscriptions.Add(new StripeSubscriptionProj
            {
                SubscriptionId = "sub_existing_other_account",
                CustomerId = "cus_existing",
                Status = "active",
                SourceSyncedAt = _clock.UtcNow.AddSeconds(-30),
            });
            db.SaveChanges();
        }

        var result = await Service().ExecuteAsync(
            intentId, "op", "price_1", "cus_preferred", false, CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("linked Stripe customer already has a current subscription");
        _server.LogEntries.Should().BeEmpty();
    }

    [Fact]
    public async Task Execute_refuses_an_orphan_active_subscription_link_before_creating_a_customer()
    {
        var (clientId, intentId) = Seed(priceId: "price_1", withCustomerLink: false);
        using (var db = _db.CreateContext())
        {
            db.IdentityLinks.Add(new IdentityLink
            {
                Id = Guid.NewGuid(), ClientId = clientId, System = ExternalSystem.Stripe,
                Kind = LinkKind.Subscription, ExternalId = "sub_orphan",
                VerifiedAt = _clock.UtcNow, CreatedAt = _clock.UtcNow,
            });
            db.StripeSubscriptions.Add(new StripeSubscriptionProj
            {
                SubscriptionId = "sub_orphan", CustomerId = "cus_orphan",
                Status = "active", SourceSyncedAt = _clock.UtcNow,
            });
            db.SaveChanges();
        }

        var result = await Service().ExecuteAsync(
            intentId, "op", "price_1", null, true, CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("no matching active customer link");
        _server.LogEntries.Should().BeEmpty("the incomplete cluster must block before any Stripe write");
        await using var verify = _db.CreateContext();
        var intentAfter = await verify.ConvertIntents.SingleAsync(i => i.Id == intentId);
        intentAfter.State.Should().Be(ConvertIntentState.Drafted);
        intentAfter.BillingStartedAt.Should().BeNull();
    }

    [Fact]
    public async Task Retry_uses_the_frozen_approved_target_even_if_current_data_changed()
    {
        var (clientId, intentId) = Seed(priceId: "price_1", withCustomerLink: true);
        using (var db = _db.CreateContext())
        {
            db.IdentityLinks.Add(new IdentityLink
            {
                Id = Guid.NewGuid(), ClientId = clientId, System = ExternalSystem.Stripe,
                Kind = LinkKind.Customer, ExternalId = "cus_changed_later", CreatedAt = _clock.UtcNow,
            });
            db.Clients.Single(c => c.Id == clientId).PreferredStripeCustomerId = "cus_changed_later";

            var frozen = new ConvertDraft(
                true,
                "Would create the approved subscription.",
                AccountType.Own,
                "price_1",
                "cus_existing",
                false,
                "USD",
                []);
            var intent = db.ConvertIntents.Single(i => i.Id == intentId);
            intent.DraftedActionJson = JsonSerializer.Serialize(frozen);
            intent.StripeCustomerId = "cus_existing";
            intent.BillingStartedAt = _clock.UtcNow.AddMinutes(-10);
            intent.UpdatedAt = _clock.UtcNow.AddMinutes(-6);
            db.SaveChanges();
        }
        StubSubscription("cus_existing");

        var result = await Service().ExecuteAsync(
            intentId, "op", "price_1", "cus_existing", false, CancellationToken.None);

        result.Ok.Should().BeTrue(result.Message);
        _server.LogEntries.Should().ContainSingle(e =>
            e.RequestMessage.Path == "/v1/subscriptions" && e.RequestMessage.Method == "POST");
        await using var verify = _db.CreateContext();
        (await verify.ConvertIntents.SingleAsync(i => i.Id == intentId)).StripeCustomerId
            .Should().Be("cus_existing");
    }

    [Fact]
    public async Task Retry_keeps_the_attempt_frozen_when_its_target_customer_link_was_invalidated()
    {
        var (clientId, intentId) = Seed(priceId: "price_1", withCustomerLink: true);
        var startedAt = FreezeForRetry(intentId);
        using (var db = _db.CreateContext())
        {
            db.IdentityLinks.Single(l => l.ClientId == clientId
                                         && l.System == ExternalSystem.Stripe
                                         && l.Kind == LinkKind.Customer
                                         && l.ExternalId == "cus_existing").InvalidatedAt = _clock.UtcNow.AddMinutes(-1);
            db.SaveChanges();
        }

        var result = await Service().ExecuteAsync(
            intentId, "op", "price_1", "cus_existing", false, CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("no longer an active mapping");
        _server.LogEntries.Should().NotContain(e => e.RequestMessage != null && e.RequestMessage.Method == "POST");
        await using var verify = _db.CreateContext();
        var intentAfter = await verify.ConvertIntents.SingleAsync(i => i.Id == intentId);
        intentAfter.State.Should().Be(ConvertIntentState.Drafted);
        intentAfter.BillingStartedAt.Should().Be(startedAt);
        (await verify.IdentityLinks.AnyAsync(l => l.ClientId == clientId
                                                  && l.System == ExternalSystem.Stripe
                                                  && l.Kind == LinkKind.Subscription
                                                  && l.InvalidatedAt == null)).Should().BeFalse();
    }

    [Fact]
    public async Task Retry_keeps_the_attempt_frozen_when_an_orphan_subscription_mapping_appears()
    {
        var (clientId, intentId) = Seed(priceId: "price_1", withCustomerLink: true);
        var startedAt = FreezeForRetry(intentId);
        using (var db = _db.CreateContext())
        {
            db.IdentityLinks.Add(new IdentityLink
            {
                Id = Guid.NewGuid(), ClientId = clientId, System = ExternalSystem.Stripe,
                Kind = LinkKind.Subscription, ExternalId = "sub_orphan_retry",
                VerifiedAt = _clock.UtcNow, CreatedAt = _clock.UtcNow,
            });
            db.StripeSubscriptions.Add(new StripeSubscriptionProj
            {
                SubscriptionId = "sub_orphan_retry", CustomerId = "cus_orphan_retry",
                Status = "active", SourceSyncedAt = _clock.UtcNow,
            });
            db.SaveChanges();
        }

        var result = await Service().ExecuteAsync(
            intentId, "op", "price_1", "cus_existing", false, CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("cannot be safely matched");
        result.Message.Should().Contain("remains frozen");
        _server.LogEntries.Should().NotContain(e => e.RequestMessage != null && e.RequestMessage.Method == "POST");
        await using var verify = _db.CreateContext();
        var intentAfter = await verify.ConvertIntents.SingleAsync(i => i.Id == intentId);
        intentAfter.State.Should().Be(ConvertIntentState.Drafted);
        intentAfter.BillingStartedAt.Should().Be(startedAt);
        intentAfter.StripeSubscriptionId.Should().BeNull();
    }

    [Fact]
    public async Task Retry_keeps_the_attempt_frozen_while_customer_ownership_is_unresolved()
    {
        var (clientId, intentId) = Seed(priceId: "price_1", withCustomerLink: true);
        var startedAt = FreezeForRetry(intentId);
        using (var db = _db.CreateContext())
        {
            db.InvestigationItems.Add(new InvestigationItem
            {
                Id = Guid.NewGuid(),
                ClientId = clientId,
                Kind = InvestigationKind.DuplicateStripeCustomer,
                Status = InvestigationStatus.Open,
                Detail = "Multiple Stripe customers still need ownership confirmation.",
                CreatedAt = _clock.UtcNow,
            });
            db.SaveChanges();
        }

        var result = await Service().ExecuteAsync(
            intentId, "op", "price_1", "cus_existing", false, CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("ownership is unconfirmed");
        result.Message.Should().Contain("remains frozen");
        _server.LogEntries.Should().NotContain(e => e.RequestMessage != null && e.RequestMessage.Method == "POST");
        await using var verify = _db.CreateContext();
        var intentAfter = await verify.ConvertIntents.SingleAsync(i => i.Id == intentId);
        intentAfter.State.Should().Be(ConvertIntentState.Drafted);
        intentAfter.BillingStartedAt.Should().Be(startedAt);
        intentAfter.StripeSubscriptionId.Should().BeNull();
    }

    [Fact]
    public async Task Retry_keeps_the_attempt_frozen_when_a_conflicting_live_subscription_exists()
    {
        var (_, intentId) = Seed(priceId: "price_1", withCustomerLink: true);
        var startedAt = FreezeForRetry(intentId);
        _server.ResetMappings();
        _server.Given(Request.Create().WithPath("/v1/subscriptions").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                data = new[]
                {
                    new
                    {
                        id = "sub_other_live",
                        customer = "cus_existing",
                        status = "active",
                        currency = "usd",
                        metadata = new Dictionary<string, string>(),
                        items = new { data = new[] { new { price = new { id = "price_1" } } } },
                    },
                },
                has_more = false,
            }));

        var result = await Service().ExecuteAsync(
            intentId, "op", "price_1", "cus_existing", false, CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("another live subscription");
        result.Message.Should().Contain("remains frozen");
        _server.LogEntries.Should().NotContain(e => e.RequestMessage != null && e.RequestMessage.Method == "POST");
        await using var verify = _db.CreateContext();
        var intentAfter = await verify.ConvertIntents.SingleAsync(i => i.Id == intentId);
        intentAfter.State.Should().Be(ConvertIntentState.Drafted);
        intentAfter.BillingStartedAt.Should().Be(startedAt);
        intentAfter.StripeSubscriptionId.Should().BeNull();
    }

    [Theory]
    [InlineData("canceled", ConvertIntentState.Reversed)]
    [InlineData("incomplete_expired", ConvertIntentState.Failed)]
    public async Task Retry_records_its_own_terminal_subscription_and_closes_without_another_post(
        string subscriptionStatus,
        ConvertIntentState expectedState)
    {
        var (clientId, intentId) = Seed(priceId: "price_1", withCustomerLink: true);
        FreezeForRetry(intentId);
        _server.ResetMappings();
        _server.Given(Request.Create().WithPath("/v1/subscriptions").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                data = new[]
                {
                    new
                    {
                        id = "sub_terminal_recovered",
                        customer = "cus_existing",
                        status = subscriptionStatus,
                        currency = "usd",
                        metadata = new Dictionary<string, string>
                        {
                            ["convert_intent_id"] = intentId.ToString(),
                        },
                        items = new { data = new[] { new { price = new { id = "price_1" } } } },
                    },
                },
                has_more = false,
            }));

        var result = await Service().ExecuteAsync(
            intentId, "op", "price_1", "cus_existing", false, CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Message.Should().Contain($"it is {subscriptionStatus}");
        result.Message.Should().Contain($"closed as {expectedState}");
        _server.LogEntries.Should().NotContain(e => e.RequestMessage != null && e.RequestMessage.Method == "POST");
        await using var verify = _db.CreateContext();
        var intentAfter = await verify.ConvertIntents.SingleAsync(i => i.Id == intentId);
        intentAfter.State.Should().Be(expectedState);
        intentAfter.StripeSubscriptionId.Should().Be("sub_terminal_recovered");
        intentAfter.ExpiresAt.Should().BeNull();
        (await verify.IdentityLinks.AnyAsync(l => l.ClientId == clientId
                                                  && l.System == ExternalSystem.Stripe
                                                  && l.Kind == LinkKind.Subscription
                                                  && l.ExternalId == "sub_terminal_recovered"
                                                  && l.InvalidatedAt == null)).Should().BeTrue();
    }

    [Fact]
    public async Task Immediate_retry_is_held_by_the_billing_attempt_lease()
    {
        var (_, intentId) = Seed(priceId: "price_1", withCustomerLink: true);
        using (var db = _db.CreateContext())
        {
            var frozen = new ConvertDraft(
                true, "Approved", AccountType.Own, "price_1", "cus_existing", false, "USD", []);
            var intent = db.ConvertIntents.Single(i => i.Id == intentId);
            intent.DraftedActionJson = JsonSerializer.Serialize(frozen);
            intent.BillingStartedAt = _clock.UtcNow.AddMinutes(-1);
            intent.UpdatedAt = _clock.UtcNow;
            db.SaveChanges();
        }

        var result = await Service().ExecuteAsync(
            intentId, "op", "price_1", "cus_existing", false, CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("already in progress");
        _server.LogEntries.Should().BeEmpty();
    }

    [Fact]
    public async Task Live_check_blocks_a_subscription_created_after_the_last_completed_sweep()
    {
        var (_, intentId) = Seed(priceId: "price_1", withCustomerLink: true);
        _server.ResetMappings();
        _server.Given(Request.Create().WithPath("/v1/subscriptions").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                data = new[]
                {
                    new
                    {
                        id = "sub_created_after_sync", customer = "cus_existing", status = "active",
                        currency = "usd", metadata = new Dictionary<string, string>(),
                        items = new { data = Array.Empty<object>() },
                    },
                },
                has_more = false,
            }));

        var result = await Service().ExecuteAsync(
            intentId, "op", "price_1", "cus_existing", false, CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("already has a live subscription");
        _server.LogEntries.Should().NotContain(e => e.RequestMessage.Method == "POST");
        await using var verify = _db.CreateContext();
        (await verify.ConvertIntents.SingleAsync(i => i.Id == intentId)).BillingStartedAt.Should().BeNull();
    }

    [Fact]
    public async Task Retry_recovers_the_subscription_created_by_this_intent_without_a_second_post()
    {
        var (_, intentId) = Seed(priceId: "price_1", withCustomerLink: true);
        using (var db = _db.CreateContext())
        {
            var frozen = new ConvertDraft(
                true, "Approved", AccountType.Own, "price_1", "cus_existing", false, "USD", []);
            var intent = db.ConvertIntents.Single(i => i.Id == intentId);
            intent.DraftedActionJson = JsonSerializer.Serialize(frozen);
            intent.StripeCustomerId = "cus_existing";
            intent.BillingStartedAt = _clock.UtcNow.AddMinutes(-10);
            intent.UpdatedAt = _clock.UtcNow.AddMinutes(-6);
            db.SaveChanges();
        }
        _server.ResetMappings();
        _server.Given(Request.Create().WithPath("/v1/subscriptions").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                data = new[]
                {
                    new
                    {
                        id = "sub_recovered", customer = "cus_existing", status = "active",
                        currency = "usd",
                        metadata = new Dictionary<string, string> { ["convert_intent_id"] = intentId.ToString() },
                        items = new { data = new[] { new { price = new { id = "price_1" } } } },
                    },
                },
                has_more = false,
            }));

        var result = await Service().ExecuteAsync(
            intentId, "op", "price_1", "cus_existing", false, CancellationToken.None);

        result.Ok.Should().BeTrue(result.Message);
        _server.LogEntries.Should().NotContain(e => e.RequestMessage.Method == "POST");
        await using var verify = _db.CreateContext();
        var intentAfter = await verify.ConvertIntents.SingleAsync(i => i.Id == intentId);
        intentAfter.State.Should().Be(ConvertIntentState.AwaitingPayment);
        intentAfter.StripeSubscriptionId.Should().Be("sub_recovered");
    }

    [Fact]
    public async Task Retry_freezes_when_multiple_subscriptions_claim_the_same_conversion()
    {
        var (_, intentId) = Seed(priceId: "price_1", withCustomerLink: true);
        var startedAt = FreezeForRetry(intentId);
        _server.ResetMappings();
        var metadata = new Dictionary<string, string> { ["convert_intent_id"] = intentId.ToString() };
        _server.Given(Request.Create().WithPath("/v1/subscriptions").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                data = new[]
                {
                    new
                    {
                        id = "sub_duplicate_a", customer = "cus_existing", status = "active",
                        currency = "usd", metadata,
                        items = new { data = new[] { new { price = new { id = "price_1" } } } },
                    },
                    new
                    {
                        id = "sub_duplicate_b", customer = "cus_existing", status = "trialing",
                        currency = "usd", metadata,
                        items = new { data = new[] { new { price = new { id = "price_1" } } } },
                    },
                },
                has_more = false,
            }));

        var result = await Service().ExecuteAsync(
            intentId, "op", "price_1", "cus_existing", false, CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Message.Should().ContainEquivalentOf("multiple Stripe subscriptions");
        _server.LogEntries.Should().NotContain(entry => entry.RequestMessage.Method == "POST");
        await using var verify = _db.CreateContext();
        var intent = await verify.ConvertIntents.FindAsync(intentId);
        intent!.State.Should().Be(ConvertIntentState.Drafted);
        intent.BillingStartedAt.Should().Be(startedAt);
    }

    [Fact]
    public async Task Retry_freezes_when_recovered_subscription_price_differs_from_approved_price()
    {
        var (_, intentId) = Seed(priceId: "price_1", withCustomerLink: true);
        var startedAt = FreezeForRetry(intentId);
        _server.ResetMappings();
        _server.Given(Request.Create().WithPath("/v1/subscriptions").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                data = new[]
                {
                    new
                    {
                        id = "sub_wrong_price", customer = "cus_existing", status = "active",
                        currency = "usd",
                        metadata = new Dictionary<string, string>
                        {
                            ["convert_intent_id"] = intentId.ToString(),
                        },
                        items = new { data = new[] { new { price = new { id = "price_other" } } } },
                    },
                },
                has_more = false,
            }));

        var result = await Service().ExecuteAsync(
            intentId, "op", "price_1", "cus_existing", false, CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Message.Should().ContainEquivalentOf("price does not exactly match");
        _server.LogEntries.Should().NotContain(entry => entry.RequestMessage.Method == "POST");
        await using var verify = _db.CreateContext();
        var intent = await verify.ConvertIntents.FindAsync(intentId);
        intent!.State.Should().Be(ConvertIntentState.Drafted);
        intent.BillingStartedAt.Should().Be(startedAt);
    }

    [Fact]
    public async Task Definite_Stripe_4xx_unlocks_the_unwritten_conversion_for_correction()
    {
        var (_, intentId) = Seed(priceId: "price_1", withCustomerLink: true);
        _server.ResetMappings();
        _server.Given(Request.Create().WithPath("/v1/subscriptions").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBodyAsJson(new { data = Array.Empty<object>(), has_more = false }));
        _server.Given(Request.Create().WithPath("/v1/subscriptions").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(400)
                .WithBodyAsJson(new { error = new { message = "invalid price" } }));

        var result = await Service().ExecuteAsync(
            intentId, "op", "price_1", "cus_existing", false, CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("unlocked");
        await using var verify = _db.CreateContext();
        var intentAfter = await verify.ConvertIntents.SingleAsync(i => i.Id == intentId);
        intentAfter.State.Should().Be(ConvertIntentState.Drafted);
        intentAfter.BillingStartedAt.Should().BeNull();
    }

    [Fact]
    public async Task Execute_and_active_draft_fail_closed_for_a_legacy_merged_client()
    {
        var (clientId, intentId) = Seed(priceId: "price_1", withCustomerLink: true);
        var startedAt = FreezeForRetry(intentId);
        using (var db = _db.CreateContext())
        {
            var survivor = new Client
            {
                Id = Guid.NewGuid(), BusinessName = "Surviving account",
                ContractType = ContractType.Paid, AccountType = AccountType.Own,
                CreatedAt = _clock.UtcNow,
            };
            db.Clients.Add(survivor);
            var retired = db.Clients.Single(client => client.Id == clientId);
            retired.MergedIntoClientId = survivor.Id;
            retired.MergedAt = _clock.UtcNow;
            db.SaveChanges();
        }

        var result = await Service().ExecuteAsync(
            intentId, "op", "price_1", "cus_existing", false, CancellationToken.None);
        var draft = await new ConvertService(_db.Factory, _clock)
            .GetActiveDraftAsync(clientId, CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Message.Should().ContainEquivalentOf("merged");
        draft.Should().BeNull();
        _server.LogEntries.Should().BeEmpty();
        await using var verify = _db.CreateContext();
        (await verify.ConvertIntents.FindAsync(intentId))!.BillingStartedAt.Should().Be(startedAt);
    }

    [Fact]
    public async Task Kill_switch_engaged_after_customer_create_blocks_subscription_post_and_keeps_frozen_target()
    {
        var (_, intentId) = Seed(priceId: "price_1", withCustomerLink: false);
        _server.Given(Request.Create().WithPath("/v1/customers").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBodyAsJson(new { id = "cus_created_before_stop", created = 1_700_000_000 }));
        StubSubscription("cus_created_before_stop");
        var killSwitch = new KillSwitchService(_db.Factory, _clock);
        var service = Service(async (stage, ct) =>
        {
            if (stage == "subscription")
                await killSwitch.EngageAsync("safety-test", "stop between Stripe writes", ct);
        });

        var result = await service.ExecuteAsync(
            intentId, "op", "price_1", null, true, CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Message.Should().ContainEquivalentOf("kill switch");
        _server.LogEntries.Count(entry => entry.RequestMessage.Path == "/v1/customers"
                                          && entry.RequestMessage.Method == "POST").Should().Be(1);
        _server.LogEntries.Should().NotContain(entry =>
            entry.RequestMessage.Path == "/v1/subscriptions"
            && entry.RequestMessage.Method == "POST");
        await using var verify = _db.CreateContext();
        var intent = await verify.ConvertIntents.FindAsync(intentId);
        intent!.BillingStartedAt.Should().NotBeNull();
        intent.StripeCustomerId.Should().Be("cus_created_before_stop");
        (await verify.IdentityLinks.AnyAsync(link =>
            link.Kind == LinkKind.Customer
            && link.ExternalId == "cus_created_before_stop"
            && link.InvalidatedAt == null)).Should().BeTrue();
    }

    [Fact]
    public async Task Cancel_cancels_the_subscription_and_reverses_the_conversion()
    {
        var (_, intentId) = Seed(priceId: "price_1", withCustomerLink: true,
            state: ConvertIntentState.Paid, subscriptionId: "sub_x");
        _server.Given(Request.Create().WithPath("/v1/subscriptions/sub_x").UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { id = "sub_x", status = "canceled" }));

        var result = await Service().CancelAsync(intentId, "op", CancellationToken.None);

        result.Ok.Should().BeTrue(result.Message);
        await using var db = _db.CreateContext();
        db.ConvertIntents.Single(i => i.Id == intentId).State.Should().Be(ConvertIntentState.Reversed);
    }

    /// <summary>
    /// The double-billing guard: a client with a completed (Paid/Closed) conversion must not be
    /// convertible again — a second Convert would bill a SECOND live subscription under a different
    /// idempotency key. Independent of ContractType so pre-fix data and drift are also caught.
    /// </summary>
    [Fact]
    public async Task Convert_refuses_a_client_that_was_already_converted_and_billed()
    {
        var (clientId, _) = Seed(priceId: "price_1", withCustomerLink: true,
            state: ConvertIntentState.Closed, subscriptionId: "sub_done");
        // Force the pre-fix condition: client still reads Trial despite a completed conversion.
        using (var db = _db.CreateContext())
        {
            db.Clients.Single(c => c.Id == clientId).ContractType = ContractType.Trial;
            db.SaveChanges();
        }

        var convert = new ConvertService(_db.Factory, _clock);
        var result = await convert.CreateIntentAsync(clientId, AccountType.Own, null, "op", CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("already been converted");

        await using var check = _db.CreateContext();
        check.ConvertIntents.Count(i => i.ClientId == clientId).Should().Be(1); // no second intent
    }

    [Fact]
    public async Task Cancel_refuses_when_nothing_has_been_billed()
    {
        var (_, intentId) = Seed(priceId: "price_1", withCustomerLink: false, state: ConvertIntentState.Drafted);

        var result = await Service().CancelAsync(intentId, "op", CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("hasn't been billed");
        _server.LogEntries.Should().BeEmpty();
    }
}
