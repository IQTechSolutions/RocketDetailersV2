using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RD.Domain;
using RD.Domain.Entities;
using RD.Infrastructure.Webhooks;
using RD.Tests.Integration.TestInfra;

namespace RD.Tests.Integration;

public sealed class StripeWebhookIngestorTests : IDisposable
{
    private readonly SyncTestDb _db = new();
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _db.Dispose();

    private StripeWebhookIngestor CreateIngestor() =>
        new(_db.Factory, _clock, NullLogger<StripeWebhookIngestor>.Instance);

    [Fact]
    public async Task InvoicePaid_CreatesOneChargePaidLedgerEntry_AndInvoiceProjection()
    {
        var clientId = _db.SeedClientWithLink(
            ExternalSystem.Stripe, LinkKind.Customer, "cus_1",
            (ExternalSystem.Stripe, LinkKind.Subscription, "sub_1"));

        var payload = InvoicePaidEvent("evt_1", "in_1", "cus_1", "sub_1", amountPaid: 2500);

        var result = await CreateIngestor().IngestAsync("evt_1", "invoice.paid", payload, CancellationToken.None);
        result.Should().Be(WebhookIngestResult.Processed);

        await using var db = _db.CreateContext();

        var invoice = db.StripeInvoices.Single();
        invoice.InvoiceId.Should().Be("in_1");
        invoice.ClientId.Should().Be(clientId);
        invoice.Status.Should().Be("paid");
        invoice.SubscriptionId.Should().Be("sub_1");
        invoice.AmountPaid.Should().Be(25.00m);        // 2500 minor units
        invoice.SourceSyncedAt.Should().Be(_clock.UtcNow);

        var ledger = db.LedgerEntries.ToList();
        ledger.Should().ContainSingle();
        ledger[0].Type.Should().Be(LedgerEntryType.ChargePaid);
        ledger[0].SignedAmount.Should().Be(25.00m);
        ledger[0].SourceObjectId.Should().Be("in_1");
        ledger[0].ClientId.Should().Be(clientId);
        ledger[0].SourceSystem.Should().Be(ExternalSystem.Stripe);

        var inbox = db.WebhookInbox.Single();
        inbox.ExternalEventId.Should().Be("evt_1");
        inbox.System.Should().Be(ExternalSystem.Stripe);
        inbox.Status.Should().Be(WebhookStatus.Processed);
        inbox.ProcessedAt.Should().NotBeNull();
        inbox.EntityRef.Should().Be("in_1");
    }

    [Fact]
    public async Task ReplayingSameEventId_IsANoOp_ExactlyOneLedgerEntry_AndOneInboxRow()
    {
        var clientId = _db.SeedClientWithLink(ExternalSystem.Stripe, LinkKind.Customer, "cus_1");
        var payload = InvoicePaidEvent("evt_replay", "in_1", "cus_1", subscriptionId: null, amountPaid: 2500);

        var ingestor = CreateIngestor();
        var first = await ingestor.IngestAsync("evt_replay", "invoice.paid", payload, CancellationToken.None);
        var second = await ingestor.IngestAsync("evt_replay", "invoice.paid", payload, CancellationToken.None);

        first.Should().Be(WebhookIngestResult.Processed);
        second.Should().Be(WebhookIngestResult.AlreadyProcessed);

        await using var db = _db.CreateContext();
        db.WebhookInbox.Count().Should().Be(1);                                    // deduped on event id
        db.LedgerEntries.Count(l => l.Type == LedgerEntryType.ChargePaid).Should().Be(1);
        db.StripeInvoices.Single().ClientId.Should().Be(clientId);
    }

    [Fact]
    public async Task ProcessingError_MarksRowPoisoned_EventStillRecorded_NoSideEffects()
    {
        _db.SeedClientWithLink(ExternalSystem.Stripe, LinkKind.Customer, "cus_1");

        // A well-formed envelope whose invoice object has no id — processing throws.
        var payload = """{"id":"evt_bad","type":"invoice.paid","data":{"object":{}}}""";

        var result = await CreateIngestor().IngestAsync("evt_bad", "invoice.paid", payload, CancellationToken.None);
        result.Should().Be(WebhookIngestResult.Poisoned);

        await using var db = _db.CreateContext();

        var inbox = db.WebhookInbox.Single();
        inbox.ExternalEventId.Should().Be("evt_bad");          // recorded, never lost
        inbox.Status.Should().Be(WebhookStatus.Poisoned);
        inbox.Attempts.Should().Be(1);
        inbox.LastError.Should().NotBeNullOrEmpty();
        inbox.ProcessedAt.Should().BeNull();

        // No half-applied side effects survived the rollback.
        db.StripeInvoices.Should().BeEmpty();
        db.LedgerEntries.Should().BeEmpty();
    }

    [Fact]
    public async Task SubscriptionDeleted_FlipsProjectionStatusToCanceled()
    {
        var clientId = _db.SeedClientWithLink(ExternalSystem.Stripe, LinkKind.Subscription, "sub_9");

        // Pre-existing active projection — the event must flip it, not just create one.
        await using (var seed = _db.CreateContext())
        {
            seed.StripeSubscriptions.Add(new StripeSubscriptionProj
            {
                SubscriptionId = "sub_9",
                CustomerId = "cus_9",
                Status = "active",
                ClientId = clientId,
                SourceSyncedAt = _clock.UtcNow.AddDays(-1),
            });
            await seed.SaveChangesAsync();
        }

        var payload = SubscriptionDeletedEvent("evt_sub_del", "sub_9", "cus_9", canceledAt: 1784592000);

        var result = await CreateIngestor().IngestAsync(
            "evt_sub_del", "customer.subscription.deleted", payload, CancellationToken.None);
        result.Should().Be(WebhookIngestResult.Processed);

        await using var db = _db.CreateContext();
        var proj = db.StripeSubscriptions.Single(s => s.SubscriptionId == "sub_9");
        proj.Status.Should().Be("canceled");
        proj.CanceledAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1784592000));
        proj.ClientId.Should().Be(clientId);

        db.WebhookInbox.Single().Status.Should().Be(WebhookStatus.Processed);
    }

    [Fact]
    public async Task UnknownEventType_IsRecordedProcessed_WithNoSideEffect()
    {
        var payload = """{"id":"evt_unknown","type":"charge.refunded","data":{"object":{"id":"ch_1"}}}""";

        var result = await CreateIngestor().IngestAsync(
            "evt_unknown", "charge.refunded", payload, CancellationToken.None);
        result.Should().Be(WebhookIngestResult.Processed);

        await using var db = _db.CreateContext();
        db.WebhookInbox.Single().Status.Should().Be(WebhookStatus.Processed);
        db.StripeInvoices.Should().BeEmpty();
        db.StripeSubscriptions.Should().BeEmpty();
        db.LedgerEntries.Should().BeEmpty();
    }

    private static string InvoicePaidEvent(
        string eventId, string invoiceId, string customerId, string? subscriptionId, long amountPaid)
    {
        var subscriptionField = subscriptionId is null ? "null" : $"\"{subscriptionId}\"";
        return $$"""
        {
          "id": "{{eventId}}",
          "type": "invoice.paid",
          "data": {
            "object": {
              "id": "{{invoiceId}}",
              "customer": "{{customerId}}",
              "subscription": {{subscriptionField}},
              "status": "paid",
              "amount_due": {{amountPaid}},
              "amount_paid": {{amountPaid}},
              "currency": "usd",
              "hosted_invoice_url": "https://invoice.stripe.com/i/{{invoiceId}}",
              "created": 1784505600,
              "due_date": null,
              "status_transitions": { "paid_at": 1784592000 }
            }
          }
        }
        """;
    }

    private static string SubscriptionDeletedEvent(
        string eventId, string subscriptionId, string customerId, long canceledAt) => $$"""
        {
          "id": "{{eventId}}",
          "type": "customer.subscription.deleted",
          "data": {
            "object": {
              "id": "{{subscriptionId}}",
              "customer": "{{customerId}}",
              "status": "canceled",
              "canceled_at": {{canceledAt}},
              "currency": "usd",
              "items": { "data": [ { "price": { "unit_amount": 10000, "currency": "usd", "recurring": { "interval": "month" } } } ] }
            }
          }
        }
        """;
}
