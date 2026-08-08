using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RD.Domain;
using RD.Domain.Entities;
using RD.Infrastructure.Enforcement;
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
        invoice.PaidAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1784592000));
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
    public async Task Webhook_re_resolves_customer_owner_after_waiting_for_mapping_mutation()
    {
        var originalClientId = _db.SeedClientWithLink(
            ExternalSystem.Stripe, LinkKind.Customer, "cus_webhook_move",
            (ExternalSystem.Stripe, LinkKind.Subscription, "sub_webhook_move"));
        Guid newOwnerId;
        await using (var seed = _db.CreateContext())
        {
            var newOwner = new Client
            {
                Id = Guid.NewGuid(), BusinessName = "Webhook new owner",
                ContractType = ContractType.Paid, AccountType = AccountType.Own,
                CreatedAt = _clock.UtcNow,
            };
            newOwnerId = newOwner.Id;
            seed.Clients.Add(newOwner);
            await seed.SaveChangesAsync();
        }
        var payload = InvoicePaidEvent(
            "evt_owner_move", "in_owner_move", "cus_webhook_move", "sub_webhook_move", 2500);

        await using var ownershipDb = _db.CreateContext();
        var ownershipFence = await ClientMutationFence.AcquireMappingOwnershipAsync(ownershipDb);
        Task<WebhookIngestResult>? ingestTask = null;
        try
        {
            ingestTask = CreateIngestor().IngestAsync(
                "evt_owner_move", "invoice.paid", payload, CancellationToken.None);
            await Task.Delay(100);
            ingestTask.IsCompleted.Should().BeFalse("webhook attribution must wait for mapping ownership");

            var movedLinks = await ownershipDb.IdentityLinks
                .Where(link => link.ClientId == originalClientId
                               && link.System == ExternalSystem.Stripe)
                .ToListAsync();
            foreach (var link in movedLinks)
            {
                link.ClientId = newOwnerId;
                link.LinkVersion++;
            }
            await ownershipDb.SaveChangesAsync();
        }
        finally
        {
            await ownershipFence.DisposeAsync();
        }

        (await ingestTask!).Should().Be(WebhookIngestResult.Processed);
        await using var verify = _db.CreateContext();
        (await verify.LedgerEntries.SingleAsync()).ClientId.Should().Be(newOwnerId);
        (await verify.StripeInvoices.SingleAsync()).ClientId.Should().Be(newOwnerId);
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

    [Fact]
    public async Task InvoicePaymentFailed_ProjectsOpenInvoice_NeverWritesLedger()
    {
        var clientId = _db.SeedClientWithLink(ExternalSystem.Stripe, LinkKind.Customer, "cus_pf");

        var payload = InvoiceEvent(
            "evt_pf", "invoice.payment_failed", "in_pf", "cus_pf", "sub_pf",
            status: "open", amountDue: 4900, amountPaid: 0);

        var result = await CreateIngestor().IngestAsync(
            "evt_pf", "invoice.payment_failed", payload, CancellationToken.None);
        result.Should().Be(WebhookIngestResult.Processed);

        await using var db = _db.CreateContext();

        var invoice = db.StripeInvoices.Single();
        invoice.InvoiceId.Should().Be("in_pf");
        invoice.ClientId.Should().Be(clientId);
        invoice.Status.Should().Be("open");                       // the failed payment leaves it open
        invoice.AmountDue.Should().Be(49.00m);
        invoice.AmountPaid.Should().Be(0m);
        invoice.HostedInvoiceUrl.Should().Be("https://invoice.stripe.com/i/in_pf"); // dunning needs this URL

        db.LedgerEntries.Should().BeEmpty();                      // no payment ⇒ no money movement
        var inbox = db.WebhookInbox.Single();
        inbox.Status.Should().Be(WebhookStatus.Processed);
        inbox.EntityRef.Should().Be("in_pf");
    }

    [Fact]
    public async Task ResolveClient_SubscriptionLinkWinsOverCustomer_AndInvalidatedLinkIsRefused()
    {
        // Client A owns the subscription link, client B the customer link.
        var subClientId = _db.SeedClientWithLink(ExternalSystem.Stripe, LinkKind.Subscription, "sub_x");
        var cusClientId = _db.SeedClientWithLink(ExternalSystem.Stripe, LinkKind.Customer, "cus_x");
        var ingestor = CreateIngestor();

        // Both ids present ⇒ the subscription link wins.
        var first = await ingestor.IngestAsync("evt_prec_1", "invoice.payment_failed",
            InvoiceEvent("evt_prec_1", "invoice.payment_failed", "in_prec_1", "cus_x", "sub_x",
                status: "open", amountDue: 4900, amountPaid: 0), CancellationToken.None);
        first.Should().Be(WebhookIngestResult.Processed);

        await using (var db = _db.CreateContext())
        {
            db.StripeInvoices.Single(i => i.InvoiceId == "in_prec_1").ClientId.Should().Be(subClientId);

            // Mapping drift: the subscription link is invalidated — it must never route again.
            var link = db.IdentityLinks.Single(l => l.ExternalId == "sub_x");
            link.InvalidatedAt = _clock.UtcNow;
            await db.SaveChangesAsync();
        }

        // Same pair of ids ⇒ the dead subscription link is refused; the customer link resolves.
        var second = await ingestor.IngestAsync("evt_prec_2", "invoice.payment_failed",
            InvoiceEvent("evt_prec_2", "invoice.payment_failed", "in_prec_2", "cus_x", "sub_x",
                status: "open", amountDue: 4900, amountPaid: 0), CancellationToken.None);
        second.Should().Be(WebhookIngestResult.Processed);

        await using var verify = _db.CreateContext();
        verify.StripeInvoices.Single(i => i.InvoiceId == "in_prec_2").ClientId.Should().Be(cusClientId);
    }

    private static string InvoiceEvent(
        string eventId, string eventType, string invoiceId, string customerId, string? subscriptionId,
        string status, long amountDue, long amountPaid)
    {
        var subscriptionField = subscriptionId is null ? "null" : $"\"{subscriptionId}\"";
        return $$"""
        {
          "id": "{{eventId}}",
          "type": "{{eventType}}",
          "data": {
            "object": {
              "id": "{{invoiceId}}",
              "customer": "{{customerId}}",
              "subscription": {{subscriptionField}},
              "status": "{{status}}",
              "amount_due": {{amountDue}},
              "amount_paid": {{amountPaid}},
              "currency": "usd",
              "hosted_invoice_url": "https://invoice.stripe.com/i/{{invoiceId}}",
              "created": 1784505600,
              "due_date": null,
              "status_transitions": { "paid_at": null }
            }
          }
        }
        """;
    }

    [Fact]
    public async Task InvoicePaid_PromotesMatchingConversion_AndTrial()
    {
        var clientId = _db.SeedClientWithLink(
            ExternalSystem.Stripe, LinkKind.Customer, "cus_1",
            (ExternalSystem.Stripe, LinkKind.Subscription, "sub_1"),
            (ExternalSystem.Ghl, LinkKind.Contact, "ghl_c1"));

        Guid intentId;
        using (var seed = _db.CreateContext())
        {
            // Start as a TRIAL client so the promotion's Trial → Paid flip is actually proven
            // (the shared seed helper creates clients as Paid, which would pass trivially).
            seed.Clients.Single(c => c.Id == clientId).ContractType = ContractType.Trial;
            var intent = new ConvertIntent
            {
                Id = Guid.NewGuid(), ClientId = clientId, AccountType = AccountType.Own,
                State = ConvertIntentState.AwaitingPayment, StripeSubscriptionId = "sub_1",
                CreatedAt = _clock.UtcNow, UpdatedAt = _clock.UtcNow,
            };
            intentId = intent.Id;
            seed.ConvertIntents.Add(intent);
            seed.TrialPeriods.Add(new TrialPeriod
            {
                Id = Guid.NewGuid(), ClientId = clientId, StartsAt = _clock.UtcNow.AddDays(-3), Outcome = TrialOutcome.Active,
            });
            seed.SaveChanges();
        }

        var payload = InvoicePaidEvent("evt_p", "in_p", "cus_1", "sub_1", amountPaid: 9900);
        var result = await CreateIngestor().IngestAsync("evt_p", "invoice.paid", payload, CancellationToken.None);
        result.Should().Be(WebhookIngestResult.Processed);

        await using var db = _db.CreateContext();
        var promoted = db.ConvertIntents.Single(i => i.Id == intentId);
        promoted.State.Should().Be(ConvertIntentState.Paid);
        promoted.CloseTagContactId.Should().Be("ghl_c1"); // Shadow record of the close-write target
        db.TrialPeriods.Single(t => t.ClientId == clientId).Outcome.Should().Be(TrialOutcome.Promoted);
        // The client becomes a subscriber — this is what blocks a second conversion (double-billing).
        db.Clients.Single(c => c.Id == clientId).ContractType.Should().Be(ContractType.Paid);
    }

    [Fact]
    public async Task InvoicePaid_Uses_convert_intent_metadata_when_payment_beats_subscription_persistence()
    {
        var clientId = _db.SeedClientWithLink(
            ExternalSystem.Stripe, LinkKind.Customer, "cus_fast",
            (ExternalSystem.Ghl, LinkKind.Contact, "ghl_fast"));

        Guid intentId;
        using (var seed = _db.CreateContext())
        {
            seed.Clients.Single(c => c.Id == clientId).ContractType = ContractType.Trial;
            var intent = new ConvertIntent
            {
                Id = Guid.NewGuid(), ClientId = clientId, AccountType = AccountType.Own,
                State = ConvertIntentState.Drafted, BillingStartedAt = _clock.UtcNow,
                StripeCustomerId = "cus_fast",
                CreatedAt = _clock.UtcNow, UpdatedAt = _clock.UtcNow,
            };
            intentId = intent.Id;
            seed.ConvertIntents.Add(intent);
            seed.TrialPeriods.Add(new TrialPeriod
            {
                Id = Guid.NewGuid(), ClientId = clientId,
                StartsAt = _clock.UtcNow.AddDays(-1), Outcome = TrialOutcome.Active,
            });
            seed.SaveChanges();
        }

        var payload = $$"""
        {
          "id": "evt_fast",
          "type": "invoice.paid",
          "data": {
            "object": {
              "id": "in_fast",
              "customer": "cus_fast",
              "subscription": "sub_fast",
              "parent": {
                "type": "subscription_details",
                "subscription_details": {
                  "subscription": "sub_fast",
                  "metadata": { "convert_intent_id": "{{intentId}}" }
                }
              },
              "status": "paid",
              "amount_due": 9900,
              "amount_paid": 9900,
              "currency": "usd",
              "created": 1784505600,
              "status_transitions": { "paid_at": 1784592000 }
            }
          }
        }
        """;

        var result = await CreateIngestor().IngestAsync(
            "evt_fast", "invoice.paid", payload, CancellationToken.None);

        result.Should().Be(WebhookIngestResult.Processed);
        await using var db = _db.CreateContext();
        var intentAfter = await db.ConvertIntents.SingleAsync(i => i.Id == intentId);
        intentAfter.State.Should().Be(ConvertIntentState.Paid);
        intentAfter.StripeSubscriptionId.Should().Be("sub_fast");
        intentAfter.CloseTagContactId.Should().Be("ghl_fast");
        db.Clients.Single(c => c.Id == clientId).ContractType.Should().Be(ContractType.Paid);
        db.TrialPeriods.Single(t => t.ClientId == clientId).Outcome.Should().Be(TrialOutcome.Promoted);
    }

    [Fact]
    public async Task Metadata_without_a_subscription_id_never_promotes_a_conversion()
    {
        var clientId = _db.SeedClientWithLink(ExternalSystem.Stripe, LinkKind.Customer, "cus_metadata_only");
        var intentId = Guid.NewGuid();
        await using (var seed = _db.CreateContext())
        {
            seed.ConvertIntents.Add(new ConvertIntent
            {
                Id = intentId,
                ClientId = clientId,
                AccountType = AccountType.Own,
                State = ConvertIntentState.Drafted,
                BillingStartedAt = _clock.UtcNow,
                CreatedAt = _clock.UtcNow,
                UpdatedAt = _clock.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        await using (var process = _db.CreateContext())
        {
            await StripeWebhookIngestor.PromoteConversionAsync(
                process,
                subscriptionId: null,
                customerId: "cus_metadata_only",
                convertIntentId: intentId,
                resolvedClientId: clientId,
                now: _clock.UtcNow,
                ct: CancellationToken.None);
            await process.SaveChangesAsync();
        }

        await using var verify = _db.CreateContext();
        var intent = await verify.ConvertIntents.FindAsync(intentId);
        intent!.State.Should().Be(ConvertIntentState.Drafted);
        intent.StripeSubscriptionId.Should().BeNull();
    }

    [Fact]
    public async Task Stale_metadata_cannot_overwrite_a_persisted_subscription_or_promote_its_conversion()
    {
        var clientId = _db.SeedClientWithLink(
            ExternalSystem.Stripe,
            LinkKind.Customer,
            "cus_stale_metadata",
            (ExternalSystem.Stripe, LinkKind.Subscription, "sub_original"));
        var intentId = Guid.NewGuid();
        await using (var seed = _db.CreateContext())
        {
            seed.ConvertIntents.Add(new ConvertIntent
            {
                Id = intentId,
                ClientId = clientId,
                AccountType = AccountType.Own,
                State = ConvertIntentState.AwaitingPayment,
                StripeSubscriptionId = "sub_original",
                BillingStartedAt = _clock.UtcNow.AddMinutes(-1),
                CreatedAt = _clock.UtcNow,
                UpdatedAt = _clock.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        await using (var process = _db.CreateContext())
        {
            await StripeWebhookIngestor.PromoteConversionAsync(
                process,
                subscriptionId: "sub_other",
                customerId: "cus_stale_metadata",
                convertIntentId: intentId,
                resolvedClientId: clientId,
                now: _clock.UtcNow,
                ct: CancellationToken.None);
            await process.SaveChangesAsync();
        }

        await using var verify = _db.CreateContext();
        var intent = await verify.ConvertIntents.FindAsync(intentId);
        intent!.State.Should().Be(ConvertIntentState.AwaitingPayment);
        intent.StripeSubscriptionId.Should().Be("sub_original");
    }

    [Fact]
    public async Task Exact_late_payment_safely_supersedes_a_newer_unclaimed_draft()
    {
        var clientId = _db.SeedClientWithLink(
            ExternalSystem.Stripe, LinkKind.Customer, "cus_evidence",
            (ExternalSystem.Stripe, LinkKind.Subscription, "sub_exact"));
        var exactIntentId = Guid.NewGuid();
        var staleMetadataIntentId = Guid.NewGuid();
        await using (var seed = _db.CreateContext())
        {
            seed.Clients.Single(client => client.Id == clientId).AccountType = AccountType.Master;
            seed.ConvertIntents.AddRange(
                new ConvertIntent
                {
                    Id = exactIntentId, ClientId = clientId, AccountType = AccountType.Own,
                    State = ConvertIntentState.Expired, StripeCustomerId = "cus_evidence",
                    StripeSubscriptionId = "sub_exact", CreatedAt = _clock.UtcNow.AddDays(-2),
                    UpdatedAt = _clock.UtcNow.AddDays(-1),
                },
                new ConvertIntent
                {
                    Id = staleMetadataIntentId, ClientId = clientId, AccountType = AccountType.Master,
                    State = ConvertIntentState.Drafted, StripeCustomerId = "cus_evidence",
                    CreatedAt = _clock.UtcNow, UpdatedAt = _clock.UtcNow,
                });
            await seed.SaveChangesAsync();
        }

        var payload = InvoicePaidWithMetadataEvent(
            "evt_conflicting_evidence", "in_conflicting_evidence", "cus_evidence", "sub_exact",
            staleMetadataIntentId);
        var result = await CreateIngestor().IngestAsync(
            "evt_conflicting_evidence", "invoice.paid", payload, CancellationToken.None);

        result.Should().Be(WebhookIngestResult.Processed);
        await using var verify = _db.CreateContext();
        (await verify.ConvertIntents.FindAsync(exactIntentId))!.State.Should().Be(ConvertIntentState.Paid);
        var stale = await verify.ConvertIntents.FindAsync(staleMetadataIntentId);
        stale!.State.Should().Be(ConvertIntentState.Failed);
        stale.StripeSubscriptionId.Should().BeNull();
        (await verify.Clients.FindAsync(clientId))!.AccountType.Should().Be(AccountType.Own);
        var audit = await verify.InvestigationItems.SingleAsync(item => item.Kind == InvestigationKind.Other);
        audit.Status.Should().Be(InvestigationStatus.Resolved);
        audit.Detail.Should().Contain(staleMetadataIntentId.ToString());
        audit.Detail.Should().Contain(exactIntentId.ToString());
    }

    [Fact]
    public async Task Exact_late_payment_is_poisoned_when_a_newer_draft_may_have_reached_Stripe()
    {
        var clientId = _db.SeedClientWithLink(
            ExternalSystem.Stripe, LinkKind.Customer, "cus_inflight_conflict",
            (ExternalSystem.Stripe, LinkKind.Subscription, "sub_late_exact"));
        var expiredId = Guid.NewGuid();
        var inFlightId = Guid.NewGuid();
        await using (var seed = _db.CreateContext())
        {
            seed.ConvertIntents.AddRange(
                new ConvertIntent
                {
                    Id = expiredId, ClientId = clientId, AccountType = AccountType.Own,
                    State = ConvertIntentState.Expired, StripeCustomerId = "cus_inflight_conflict",
                    StripeSubscriptionId = "sub_late_exact",
                    CreatedAt = _clock.UtcNow.AddDays(-2), UpdatedAt = _clock.UtcNow.AddDays(-1),
                },
                new ConvertIntent
                {
                    Id = inFlightId, ClientId = clientId, AccountType = AccountType.Master,
                    State = ConvertIntentState.Drafted, StripeCustomerId = "cus_inflight_conflict",
                    BillingStartedAt = _clock.UtcNow.AddMinutes(-10),
                    CreatedAt = _clock.UtcNow, UpdatedAt = _clock.UtcNow,
                });
            await seed.SaveChangesAsync();
        }

        var payload = InvoicePaidWithMetadataEvent(
            "evt_inflight_conflict", "in_inflight_conflict", "cus_inflight_conflict",
            "sub_late_exact", inFlightId);
        var result = await CreateIngestor().IngestAsync(
            "evt_inflight_conflict", "invoice.paid", payload, CancellationToken.None);

        result.Should().Be(WebhookIngestResult.Poisoned);
        await using var verify = _db.CreateContext();
        (await verify.ConvertIntents.FindAsync(expiredId))!.State.Should().Be(ConvertIntentState.Expired);
        (await verify.ConvertIntents.FindAsync(inFlightId))!.State.Should().Be(ConvertIntentState.Drafted);
        verify.LedgerEntries.Should().BeEmpty();
        verify.StripeInvoices.Should().BeEmpty();
        (await verify.WebhookInbox.SingleAsync()).LastError.Should().Contain(inFlightId.ToString());
    }

    [Fact]
    public async Task Lost_ack_metadata_cannot_promote_a_frozen_intent_for_another_customer()
    {
        var clientId = _db.SeedClientWithLink(
            ExternalSystem.Stripe, LinkKind.Customer, "cus_frozen",
            (ExternalSystem.Stripe, LinkKind.Customer, "cus_other"),
            (ExternalSystem.Stripe, LinkKind.Subscription, "sub_other_customer"));
        var intentId = Guid.NewGuid();
        await using (var seed = _db.CreateContext())
        {
            seed.ConvertIntents.Add(new ConvertIntent
            {
                Id = intentId, ClientId = clientId, AccountType = AccountType.Own,
                State = ConvertIntentState.Drafted, StripeCustomerId = "cus_frozen",
                BillingStartedAt = _clock.UtcNow.AddMinutes(-10),
                CreatedAt = _clock.UtcNow, UpdatedAt = _clock.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        var payload = InvoicePaidWithMetadataEvent(
            "evt_wrong_customer", "in_wrong_customer", "cus_other", "sub_other_customer", intentId);
        var result = await CreateIngestor().IngestAsync(
            "evt_wrong_customer", "invoice.paid", payload, CancellationToken.None);

        result.Should().Be(WebhookIngestResult.Processed);
        await using var verify = _db.CreateContext();
        var intent = await verify.ConvertIntents.FindAsync(intentId);
        intent!.State.Should().Be(ConvertIntentState.Drafted);
        intent.StripeSubscriptionId.Should().BeNull();
    }

    [Fact]
    public async Task Payment_for_a_legacy_merged_conversion_is_poisoned_for_manual_reconciliation()
    {
        var survivorId = _db.SeedClientWithLink(
            ExternalSystem.Stripe, LinkKind.Customer, "cus_merged_payment",
            (ExternalSystem.Stripe, LinkKind.Subscription, "sub_merged_payment"));
        var duplicateId = Guid.NewGuid();
        var intentId = Guid.NewGuid();
        await using (var seed = _db.CreateContext())
        {
            seed.Clients.Add(new Client
            {
                Id = duplicateId, BusinessName = "Retired duplicate",
                ContractType = ContractType.Trial, AccountType = AccountType.Own,
                MergedIntoClientId = survivorId, MergedAt = _clock.UtcNow.AddDays(-1),
                CreatedAt = _clock.UtcNow.AddDays(-2),
            });
            seed.ConvertIntents.Add(new ConvertIntent
            {
                Id = intentId, ClientId = duplicateId, AccountType = AccountType.Own,
                State = ConvertIntentState.AwaitingPayment,
                StripeCustomerId = "cus_merged_payment", StripeSubscriptionId = "sub_merged_payment",
                CreatedAt = _clock.UtcNow.AddDays(-2), UpdatedAt = _clock.UtcNow.AddDays(-1),
            });
            await seed.SaveChangesAsync();
        }

        var payload = InvoicePaidEvent(
            "evt_merged_conversion", "in_merged_conversion", "cus_merged_payment",
            "sub_merged_payment", amountPaid: 9900);
        var result = await CreateIngestor().IngestAsync(
            "evt_merged_conversion", "invoice.paid", payload, CancellationToken.None);

        result.Should().Be(WebhookIngestResult.Poisoned);
        await using var verify = _db.CreateContext();
        (await verify.ConvertIntents.FindAsync(intentId))!.State.Should().Be(ConvertIntentState.AwaitingPayment);
        verify.LedgerEntries.Should().BeEmpty();
        verify.StripeInvoices.Should().BeEmpty();
        var inbox = await verify.WebhookInbox.SingleAsync();
        inbox.Status.Should().Be(WebhookStatus.Poisoned);
        inbox.LastError.Should().Contain(intentId.ToString());
        inbox.LastError.Should().Contain(survivorId.ToString());
    }

    [Fact]
    public async Task InvoicePaid_DoesNotRepromote_AnAlreadyPaidConversion()
    {
        var clientId = _db.SeedClientWithLink(
            ExternalSystem.Stripe, LinkKind.Customer, "cus_1",
            (ExternalSystem.Stripe, LinkKind.Subscription, "sub_1"));

        Guid intentId;
        using (var seed = _db.CreateContext())
        {
            var intent = new ConvertIntent
            {
                Id = Guid.NewGuid(), ClientId = clientId, AccountType = AccountType.Own,
                State = ConvertIntentState.Paid, StripeSubscriptionId = "sub_1", // already past AwaitingPayment
                CreatedAt = _clock.UtcNow, UpdatedAt = _clock.UtcNow,
            };
            intentId = intent.Id;
            seed.ConvertIntents.Add(intent);
            seed.SaveChanges();
        }

        // A later renewal invoice for the same subscription (distinct event id) must not re-promote.
        var payload = InvoicePaidEvent("evt_renewal", "in_renewal", "cus_1", "sub_1", amountPaid: 9900);
        await CreateIngestor().IngestAsync("evt_renewal", "invoice.paid", payload, CancellationToken.None);

        await using var db = _db.CreateContext();
        db.ConvertIntents.Single(i => i.Id == intentId).State.Should().Be(ConvertIntentState.Paid);
    }

    [Fact]
    public async Task InvoicePaid_RecoversAnExpiredConversion_LatePayment()
    {
        var clientId = _db.SeedClientWithLink(
            ExternalSystem.Stripe, LinkKind.Customer, "cus_1",
            (ExternalSystem.Stripe, LinkKind.Subscription, "sub_1"));

        Guid intentId;
        using (var seed = _db.CreateContext())
        {
            var intent = new ConvertIntent
            {
                Id = Guid.NewGuid(), ClientId = clientId, AccountType = AccountType.Own,
                State = ConvertIntentState.Expired, StripeSubscriptionId = "sub_1", // swept before the client paid
                CreatedAt = _clock.UtcNow, UpdatedAt = _clock.UtcNow,
            };
            intentId = intent.Id;
            seed.ConvertIntents.Add(intent);
            seed.TrialPeriods.Add(new TrialPeriod
            {
                Id = Guid.NewGuid(), ClientId = clientId, StartsAt = _clock.UtcNow.AddDays(-9), Outcome = TrialOutcome.Active,
            });
            seed.SaveChanges();
        }

        var payload = InvoicePaidEvent("evt_late", "in_late", "cus_1", "sub_1", amountPaid: 9900);
        await CreateIngestor().IngestAsync("evt_late", "invoice.paid", payload, CancellationToken.None);

        await using var db = _db.CreateContext();
        db.ConvertIntents.Single(i => i.Id == intentId).State.Should().Be(ConvertIntentState.Paid);
        db.TrialPeriods.Single(t => t.ClientId == clientId).Outcome.Should().Be(TrialOutcome.Promoted);
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

    private static string InvoicePaidWithMetadataEvent(
        string eventId,
        string invoiceId,
        string customerId,
        string subscriptionId,
        Guid convertIntentId) => $$"""
        {
          "id": "{{eventId}}",
          "type": "invoice.paid",
          "data": {
            "object": {
              "id": "{{invoiceId}}",
              "customer": "{{customerId}}",
              "subscription": "{{subscriptionId}}",
              "parent": {
                "type": "subscription_details",
                "subscription_details": {
                  "subscription": "{{subscriptionId}}",
                  "metadata": { "convert_intent_id": "{{convertIntentId}}" }
                }
              },
              "status": "paid",
              "amount_due": 9900,
              "amount_paid": 9900,
              "currency": "usd",
              "created": 1784505600,
              "status_transitions": { "paid_at": 1784592000 }
            }
          }
        }
        """;

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
