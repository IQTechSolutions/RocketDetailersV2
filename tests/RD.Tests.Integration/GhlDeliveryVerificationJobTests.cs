using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RD.Domain;
using RD.Domain.Entities;
using RD.Infrastructure.Enforcement;
using RD.Infrastructure.Gateways;
using RD.Infrastructure.Sync;
using RD.Tests.Integration.TestInfra;

namespace RD.Tests.Integration;

/// <summary>
/// F2: a GHL workflow trigger returning 200 proves nothing about delivery.
/// These tests pin the three sweep outcomes — verified on an observed outbound
/// message, escalated (alert + work item, window frozen) past the hard grace
/// with nothing observed, and auto-verified under TestMode — and, in doing so,
/// guard the regression where DunningAttempt sat in the interceptor's
/// StrictlyImmutable list and every sweep threw on SaveChanges before it could
/// record a single verification.
/// </summary>
public sealed class GhlDeliveryVerificationJobTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 07, 24, 12, 0, 0, TimeSpan.Zero);
    private readonly SyncTestDb _db = new();
    private readonly TestClock _clock = new(Now);
    private readonly EnforcementOptions _enf = new();

    /// <summary>Grace before verifying at all (10 min default); the hard grace is 3× this.</summary>
    private TimeSpan Delay => _enf.DeliveryVerificationDelay;

    private GhlDeliveryVerificationJob BuildJob(bool testMode) =>
        new(_db.Factory, _clock, Options.Create(_enf),
            Options.Create(new SafetyOptions { GhlTestMode = testMode }),
            NullLogger<GhlDeliveryVerificationJob>.Instance);

    /// <summary>Seeds an open dunning case with one triggered-but-unverified attempt.</summary>
    private Guid SeedTriggeredAttempt(Guid clientId, DateTimeOffset triggeredAt, int step = 1)
    {
        using var ctx = _db.CreateContext();
        var dunningCase = new DunningCase
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            InvoiceExternalId = "inv_" + Guid.NewGuid().ToString("N")[..8],
            OpenedAt = triggeredAt,
            WindowExpiresAt = triggeredAt.AddDays(2),
            Status = DunningCaseStatus.Open,
        };
        ctx.DunningCases.Add(dunningCase);
        var attempt = new DunningAttempt
        {
            Id = Guid.NewGuid(),
            DunningCaseId = dunningCase.Id,
            Step = step,
            DueAt = triggeredAt,
            TriggeredAt = triggeredAt,
        };
        ctx.DunningAttempts.Add(attempt);
        ctx.SaveChanges();
        return attempt.Id;
    }

    private void SeedOutboundMessage(Guid clientId, string contactId, DateTimeOffset sentAt)
    {
        using var ctx = _db.CreateContext();
        ctx.GhlMessages.Add(new GhlMessageProj
        {
            MessageId = "msg_" + Guid.NewGuid().ToString("N")[..8],
            ClientId = clientId,
            LocationId = "loc_1",
            ContactId = contactId,
            MessageType = "TYPE_SMS",
            SentAt = sentAt,
            SourceSyncedAt = sentAt,
        });
        ctx.SaveChanges();
    }

    // ── Observed outbound message ⇒ the attempt is verified, no failure recorded ──

    [Fact]
    public async Task Observed_outbound_message_marks_attempt_verified()
    {
        const string contactId = "contact_verify";
        var clientId = _db.SeedClientWithLink(ExternalSystem.Ghl, LinkKind.Contact, contactId);
        // Past the verification delay, still inside the hard grace.
        var attemptId = SeedTriggeredAttempt(clientId, triggeredAt: Now - Delay * 2);
        SeedOutboundMessage(clientId, contactId, sentAt: Now - Delay);

        await BuildJob(testMode: false).RunAsync(default);

        await using var ctx = _db.CreateContext();
        var attempt = await ctx.DunningAttempts.FindAsync(attemptId);
        attempt!.VerifiedAt.Should().Be(Now);
        attempt.FailureReason.Should().BeNull();
        (await ctx.InvestigationItems.AnyAsync()).Should().BeFalse();
        (await ctx.AlertLog.AnyAsync()).Should().BeFalse();
    }

    // ── No message past the hard grace ⇒ escalate, and the window does NOT advance ──

    [Fact]
    public async Task No_message_past_hard_grace_escalates_and_leaves_attempt_unverified()
    {
        var clientId = _db.SeedClientWithLink(ExternalSystem.Ghl, LinkKind.Contact, "contact_escalate");
        // Beyond 3× the delay (the hard grace) with no outbound message observed.
        var attemptId = SeedTriggeredAttempt(clientId, triggeredAt: Now - Delay * 4);

        await BuildJob(testMode: false).RunAsync(default);

        await using var ctx = _db.CreateContext();
        var attempt = await ctx.DunningAttempts.FindAsync(attemptId);
        attempt!.VerifiedAt.Should().BeNull(); // unverified ⇒ dunning window is frozen
        attempt.FailureReason.Should().Contain("no outbound message");

        var item = await ctx.InvestigationItems.SingleAsync(i => i.ClientId == clientId);
        item.Kind.Should().Be(InvestigationKind.DeliveryUnverified);
        item.Status.Should().Be(InvestigationStatus.Open);

        var alert = await ctx.AlertLog.SingleAsync();
        alert.Severity.Should().Be("High");
    }

    // ── TestMode ⇒ auto-verify (no real-client delivery to confirm), reason names test mode ──

    [Fact]
    public async Task Test_mode_auto_verifies_with_test_mode_reason()
    {
        var clientId = _db.SeedClientWithLink(ExternalSystem.Ghl, LinkKind.Contact, "contact_testmode");
        // Old enough to escalate were this a real send — proving TestMode short-circuits first.
        var attemptId = SeedTriggeredAttempt(clientId, triggeredAt: Now - Delay * 4);

        await BuildJob(testMode: true).RunAsync(default);

        await using var ctx = _db.CreateContext();
        var attempt = await ctx.DunningAttempts.FindAsync(attemptId);
        attempt!.VerifiedAt.Should().Be(Now);
        attempt.FailureReason.Should().Contain("test mode");
        // No real-client escalation happened.
        (await ctx.InvestigationItems.AnyAsync()).Should().BeFalse();
        (await ctx.AlertLog.AnyAsync()).Should().BeFalse();
    }

    // ── Still inside the grace window with nothing observed ⇒ leave it for the next pass ──

    [Fact]
    public async Task Within_grace_and_unobserved_leaves_attempt_untouched()
    {
        var clientId = _db.SeedClientWithLink(ExternalSystem.Ghl, LinkKind.Contact, "contact_grace");
        // Past the verification delay but inside the 3× hard grace.
        var attemptId = SeedTriggeredAttempt(clientId, triggeredAt: Now - Delay * 2);

        await BuildJob(testMode: false).RunAsync(default);

        await using var ctx = _db.CreateContext();
        var attempt = await ctx.DunningAttempts.FindAsync(attemptId);
        attempt!.VerifiedAt.Should().BeNull();
        attempt.FailureReason.Should().BeNull();
        (await ctx.InvestigationItems.AnyAsync()).Should().BeFalse();
        (await ctx.AlertLog.AnyAsync()).Should().BeFalse();
    }

    public void Dispose() => _db.Dispose();
}
