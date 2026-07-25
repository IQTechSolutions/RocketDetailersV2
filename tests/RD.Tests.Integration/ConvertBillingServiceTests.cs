using FluentAssertions;
using Microsoft.Extensions.Options;
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

    private ConvertBillingService Service()
    {
        var options = Options.Create(new StripeOptions { ApiKey = "rk_test_dummy", BaseUrl = _server.Urls[0], ApiVersion = "2025-03-31.basil" });
        var gateway = new StripeGateway(new HttpClient(), options, new RetryHelper { BaseDelay = TimeSpan.FromMilliseconds(1) });
        var killSwitch = new KillSwitchService(_db.Factory, _clock);
        return new ConvertBillingService(_db.Factory, gateway, killSwitch, _clock);
    }

    private (Guid clientId, Guid intentId) Seed(string? priceId, bool withCustomerLink,
        ConvertIntentState state = ConvertIntentState.Drafted)
    {
        using var db = _db.CreateContext();
        var pkg = new Package { Id = Guid.NewGuid(), Name = "Pkg-" + Guid.NewGuid().ToString("N")[..6], IsActive = true };
        db.Packages.Add(pkg);
        db.PackageVersions.Add(new PackageVersion
        {
            Id = Guid.NewGuid(), PackageId = pkg.Id, EffectiveFrom = _clock.UtcNow,
            DailyRate = 10m, DailyBudget = 20m, CurrencyCode = "USD", StripePriceId = priceId,
            CreatedBy = "test", CreatedAt = _clock.UtcNow,
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
            State = state, CreatedAt = _clock.UtcNow, UpdatedAt = _clock.UtcNow,
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

    [Fact]
    public async Task Execute_creates_customer_and_subscription_and_moves_to_awaiting_payment()
    {
        var (clientId, intentId) = Seed(priceId: "price_1", withCustomerLink: false);
        _server.Given(Request.Create().WithPath("/v1/customers").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { id = "cus_new", created = 1_700_000_000 }));
        StubSubscription("cus_new");

        var result = await Service().ExecuteAsync(intentId, "op", CancellationToken.None);

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

        var result = await Service().ExecuteAsync(intentId, "op", CancellationToken.None);

        result.Ok.Should().BeTrue();
        _server.LogEntries.Should().NotContain(e => e.RequestMessage.Path.Contains("/v1/customers"));

        await using var db = _db.CreateContext();
        db.ConvertIntents.Single(i => i.Id == intentId).StripeCustomerId.Should().Be("cus_existing");
    }

    [Fact]
    public async Task Execute_refuses_when_package_has_no_price_and_calls_no_stripe()
    {
        var (_, intentId) = Seed(priceId: null, withCustomerLink: false);

        var result = await Service().ExecuteAsync(intentId, "op", CancellationToken.None);

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

        var result = await Service().ExecuteAsync(intentId, "op", CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("already");
        _server.LogEntries.Should().BeEmpty();
    }
}
