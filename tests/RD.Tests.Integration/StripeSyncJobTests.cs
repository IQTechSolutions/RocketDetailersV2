using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RD.Domain;
using RD.Infrastructure.Gateways;
using RD.Infrastructure.Sync;
using RD.Tests.Integration.TestInfra;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace RD.Tests.Integration;

public sealed class StripeSyncJobTests : IDisposable
{
    private readonly SyncTestDb _db = new();
    private readonly WireMockServer _server = WireMockServer.Start();
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        _server.Stop();
        _server.Dispose();
        _db.Dispose();
    }

    private StripeSyncJob CreateJob()
    {
        var options = Options.Create(new StripeOptions { ApiKey = "rk_test_dummy", BaseUrl = _server.Urls[0], ApiVersion = "2025-03-31.basil" });
        var gateway = new StripeGateway(
            new HttpClient(), options, new RetryHelper { BaseDelay = TimeSpan.FromMilliseconds(1) });
        return new StripeSyncJob(_db.Factory, gateway, options, _clock, NullLogger<StripeSyncJob>.Instance);
    }

    [Fact]
    public async Task FullSweep_RunTwice_UpsertsProjections_LedgerIsIdempotent_SyncRunsGreen()
    {
        // Client mapped via BOTH a Stripe customer link and a subscription link.
        var clientId = _db.SeedClientWithLink(
            ExternalSystem.Stripe, LinkKind.Customer, "cus_map1",
            (ExternalSystem.Stripe, LinkKind.Subscription, "sub_1"));

        StubSubscriptionPages();
        StubInvoices();
        StubCharges();

        var job = CreateJob();
        await job.RunAsync(CancellationToken.None);
        await job.RunAsync(CancellationToken.None); // idempotency proof

        await using var db = _db.CreateContext();

        // --- Projections upserted, not duplicated.
        var subs = db.StripeSubscriptions.OrderBy(s => s.SubscriptionId).ToList();
        subs.Should().HaveCount(3);
        var sub1 = subs.Single(s => s.SubscriptionId == "sub_1");
        sub1.ClientId.Should().Be(clientId);
        sub1.Status.Should().Be("active");
        sub1.Amount.Should().Be(15.00m);            // 1500 minor units
        sub1.CurrencyCode.Should().Be("CAD");
        sub1.PriceInterval.Should().Be("day");
        subs.Single(s => s.SubscriptionId == "sub_2").ClientId.Should().BeNull();
        var sub3 = subs.Single(s => s.SubscriptionId == "sub_3");
        sub3.Status.Should().Be("canceled");
        sub3.CanceledAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1784505600));

        var invoices = db.StripeInvoices.ToList();
        invoices.Should().HaveCount(3);
        var openInvoice = invoices.Single(i => i.InvoiceId == "in_open1");
        openInvoice.Status.Should().Be("open");
        openInvoice.ClientId.Should().Be(clientId);
        openInvoice.SubscriptionId.Should().Be("sub_1");
        openInvoice.HostedInvoiceUrl.Should().Be("https://invoice.stripe.com/i/in_open1");
        openInvoice.DueDate.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1784764800));
        var paidInvoice = invoices.Single(i => i.InvoiceId == "in_paid1");
        paidInvoice.ClientId.Should().Be(clientId);
        // Basil parent.subscription_details shape must still yield the join key.
        paidInvoice.SubscriptionId.Should().Be("sub_1");
        invoices.Single(i => i.InvoiceId == "in_paid2").ClientId.Should().BeNull();

        // --- Money-IN comes from CHARGES: one invoiced (⇒ subscription, keyed by
        // the invoice id) and one direct (⇒ keyed by the charge id). The unmapped
        // customer's charge is ignored. Both survive a second run (idempotent key).
        var ledger = db.LedgerEntries.ToList();
        var paid = ledger.Where(l => l.Type == LedgerEntryType.ChargePaid).ToList();
        paid.Should().HaveCount(2);
        var subscriptionPaid = paid.Single(l => l.SourceObjectId == "in_paid1"); // has invoice ⇒ subscription
        subscriptionPaid.SignedAmount.Should().Be(15.00m);
        subscriptionPaid.ClientId.Should().Be(clientId);
        subscriptionPaid.SourceSystem.Should().Be(ExternalSystem.Stripe);
        var directPaid = paid.Single(l => l.SourceObjectId == "ch_direct1");      // no invoice ⇒ direct
        directPaid.SignedAmount.Should().Be(20.00m);
        directPaid.ClientId.Should().Be(clientId);

        // Open-past-due invoice → single ChargeFailed EVENT row, zero amount.
        var failed = ledger.Where(l => l.Type == LedgerEntryType.ChargeFailed).ToList();
        failed.Should().HaveCount(1);
        failed[0].SignedAmount.Should().Be(0m);
        failed[0].SourceObjectId.Should().Be("in_open1");
        ledger.Should().HaveCount(3); // 2 paid + 1 failed; unmapped customer's charge ignored

        // --- SyncRuns: both sweeps green, complete-sweep semantics.
        var runs = db.SyncRuns.ToList();
        runs.Should().HaveCount(2);
        runs.Should().OnlyContain(r =>
            r.System == ExternalSystem.Stripe
            && r.Status == SyncRunStatus.Completed
            && r.CompletedAt != null
            && r.ItemsSeen == 9); // 3 subscriptions + 3 invoices + 3 charges

        // --- Wire-level contract: pinned version + bearer key were sent.
        var subscriptionRequests = _server.LogEntries
            .Where(e => e.Path() == "/v1/subscriptions")
            .ToList();
        subscriptionRequests.Should().NotBeEmpty();
        subscriptionRequests.Should().OnlyContain(e =>
            e.Header("Stripe-Version") == "2025-03-31.basil" // sent when configured; unset pin = account default
            && e.Header("Authorization") == "Bearer rk_test_dummy");
    }

    private void StubSubscriptionPages()
    {
        // Page 1 (no cursor): two subscriptions, has_more → cursor is last id.
        _server.Given(Request.Create().WithPath("/v1/subscriptions")
                .WithParam("starting_after", MatchBehaviour.RejectOnMatch).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""
                {
                  "object": "list",
                  "has_more": true,
                  "data": [
                    {
                      "id": "sub_1", "customer": "cus_map1", "status": "active", "currency": "cad",
                      "canceled_at": null,
                      "items": { "data": [ { "price": { "unit_amount": 1500, "currency": "cad", "recurring": { "interval": "day" } } } ] }
                    },
                    {
                      "id": "sub_2", "customer": "cus_other", "status": "active", "currency": "usd",
                      "canceled_at": null,
                      "items": { "data": [ { "price": { "unit_amount": 10000, "currency": "usd", "recurring": { "interval": "month" } } } ] }
                    }
                  ]
                }
                """));

        // Page 2 (cursor = sub_2): final page.
        _server.Given(Request.Create().WithPath("/v1/subscriptions")
                .WithParam("starting_after", "sub_2").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""
                {
                  "object": "list",
                  "has_more": false,
                  "data": [
                    {
                      "id": "sub_3", "customer": "cus_other", "status": "canceled", "currency": "usd",
                      "canceled_at": 1784505600,
                      "items": { "data": [ { "price": { "unit_amount": 10000, "currency": "usd", "recurring": { "interval": "month" } } } ] }
                    }
                  ]
                }
                """));
    }

    private void StubInvoices()
    {
        // Open listing: one past-due invoice (due 2026-07-23 < clock 2026-07-24).
        _server.Given(Request.Create().WithPath("/v1/invoices")
                .WithParam("status", "open").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""
                {
                  "object": "list",
                  "has_more": false,
                  "data": [
                    {
                      "id": "in_open1", "customer": "cus_map1", "subscription": "sub_1",
                      "status": "open", "amount_due": 1500, "amount_paid": 0, "currency": "cad",
                      "hosted_invoice_url": "https://invoice.stripe.com/i/in_open1",
                      "created": 1784505600, "due_date": 1784764800
                    }
                  ]
                }
                """));

        // Recent listing (created[gte]): one paid invoice for the mapped customer
        // (Basil "parent" subscription shape) + one paid for an UNMAPPED customer.
        _server.Given(Request.Create().WithPath("/v1/invoices")
                .WithParam("created[gte]").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""
                {
                  "object": "list",
                  "has_more": false,
                  "data": [
                    {
                      "id": "in_paid1", "customer": "cus_map1",
                      "parent": { "subscription_details": { "subscription": "sub_1" } },
                      "status": "paid", "amount_due": 1500, "amount_paid": 1500, "currency": "cad",
                      "hosted_invoice_url": "https://invoice.stripe.com/i/in_paid1",
                      "created": 1784505600, "due_date": null,
                      "status_transitions": { "paid_at": 1784592000 }
                    },
                    {
                      "id": "in_paid2", "customer": "cus_unmapped",
                      "subscription": "sub_unmapped",
                      "status": "paid", "amount_due": 9900, "amount_paid": 9900, "currency": "usd",
                      "hosted_invoice_url": "https://invoice.stripe.com/i/in_paid2",
                      "created": 1784505600, "due_date": null,
                      "status_transitions": { "paid_at": 1784592000 }
                    }
                  ]
                }
                """));
    }

    private void StubCharges()
    {
        // A succeeded invoiced charge (⇒ subscription payment, keyed by the invoice),
        // a succeeded direct charge with no invoice (⇒ direct, keyed by the charge id),
        // and a succeeded charge for an UNMAPPED customer (must be ignored).
        _server.Given(Request.Create().WithPath("/v1/charges").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""
                {
                  "object": "list",
                  "has_more": false,
                  "data": [
                    { "id": "ch_paid1", "customer": "cus_map1", "invoice": "in_paid1",
                      "amount": 1500, "amount_refunded": 0, "currency": "cad",
                      "status": "succeeded", "paid": true, "created": 1784592000 },
                    { "id": "ch_direct1", "customer": "cus_map1", "invoice": null,
                      "amount": 2000, "amount_refunded": 0, "currency": "cad",
                      "status": "succeeded", "paid": true, "created": 1784592000 },
                    { "id": "ch_unmapped", "customer": "cus_unmapped", "invoice": null,
                      "amount": 5000, "amount_refunded": 0, "currency": "usd",
                      "status": "succeeded", "paid": true, "created": 1784592000 }
                  ]
                }
                """));
    }
}
