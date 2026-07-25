using FluentAssertions;
using Microsoft.Extensions.Options;
using RD.Infrastructure.Gateways;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace RD.Tests.Integration;

/// <summary>
/// WireMock tests for the Stripe write primitives (Convert→Bill→Close, rung B). Proves every write
/// carries an Idempotency-Key, the subscription create stamps the convert_intent_id metadata the
/// first-payment webhook correlates on, and Stripe errors surface as exceptions (never a silent
/// default). No real Stripe — a stubbed server.
/// </summary>
public sealed class StripeGatewayWriteTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose()
    {
        _server.Stop();
        _server.Dispose();
    }

    private StripeGateway Gateway()
    {
        var options = Options.Create(new StripeOptions
        {
            ApiKey = "rk_test_dummy",
            BaseUrl = _server.Urls[0],
            ApiVersion = "2025-03-31.basil",
        });
        return new StripeGateway(new HttpClient(), options, new RetryHelper { BaseDelay = TimeSpan.FromMilliseconds(1) });
    }

    [Fact]
    public async Task CreateCustomer_sends_idempotency_key_and_returns_id()
    {
        // The stub only matches when the Idempotency-Key header is present — so a missing key 404s and fails the test.
        _server.Given(Request.Create().WithPath("/v1/customers").UsingPost()
                .WithHeader("Idempotency-Key", "convert-abc"))
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBodyAsJson(new { id = "cus_new", created = 1_700_000_000 }));

        var id = await Gateway().CreateCustomerAsync("Acme Detailing", "a@b.com", "convert-abc", CancellationToken.None);

        id.Should().Be("cus_new");
    }

    [Fact]
    public async Task CreateSubscription_stamps_intent_metadata_and_idempotency_key()
    {
        _server.Given(Request.Create().WithPath("/v1/subscriptions").UsingPost()
                .WithHeader("Idempotency-Key", "convert-xyz"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                id = "sub_new",
                customer = "cus_1",
                status = "active",
                currency = "usd",
                items = new { data = new[] { new { price = new { unit_amount = 9900, currency = "usd", recurring = new { interval = "month" } } } } },
            }));

        var meta = new Dictionary<string, string> { ["convert_intent_id"] = "intent-1" };
        var dto = await Gateway().CreateSubscriptionAsync("cus_1", "price_1", meta, "convert-xyz", CancellationToken.None);

        dto.Id.Should().Be("sub_new");
        dto.CustomerId.Should().Be("cus_1");
        dto.Status.Should().Be("active");
        dto.Amount.Should().Be(99m);

        // The metadata the first-payment webhook correlates on must be in the request body.
        var body = _server.LogEntries.Last().RequestMessage.Body ?? "";
        body.Should().Contain("convert_intent_id").And.Contain("intent-1");
        body.Should().Contain("price_1"); // items[0][price]
    }

    [Fact]
    public async Task CancelSubscription_treats_missing_subscription_as_canceled()
    {
        _server.Given(Request.Create().WithPath("/v1/subscriptions/sub_gone").UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(404)
                .WithBodyAsJson(new { error = new { message = "No such subscription" } }));

        var act = async () => await Gateway().CancelSubscriptionAsync("sub_gone", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task CancelSubscription_completes_on_success()
    {
        _server.Given(Request.Create().WithPath("/v1/subscriptions/sub_1").UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { id = "sub_1", status = "canceled" }));

        var act = async () => await Gateway().CancelSubscriptionAsync("sub_1", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task CreateSubscription_surfaces_stripe_errors()
    {
        _server.Given(Request.Create().WithPath("/v1/subscriptions").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(402)
                .WithBodyAsJson(new { error = new { message = "Your card was declined." } }));

        var act = async () => await Gateway().CreateSubscriptionAsync("cus_1", "price_1", null, "k", CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }
}
