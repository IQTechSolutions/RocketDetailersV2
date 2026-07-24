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
/// F2 delivery verification: a GHL workflow trigger returning 200 proves nothing
/// about delivery. After a grace delay the job confirms an outbound message
/// actually reached the contact — verified sends let the dunning window advance,
/// unverified sends past a hard grace escalate and the window does NOT advance.
///
/// These also pin the DunningAttempt-immutability regression: the job's whole job
/// is to fill in VerifiedAt/FailureReason, which the AppendOnlyInterceptor was
/// throwing on. Every test here writes one of those columns, so a strict-immutable
/// DunningAttempt would fail them at SaveChangesAsync.
/// </summary>
public sealed class GhlDeliveryVerificationJobTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 07, 24, 12, 0, 0, TimeSpan.Zero);
    private const string ContactId = "contact_f2";
    private readonly SyncTestDb _db = new();
    private readonly TestClock _clock = new(Now);

    private GhlDeliveryVerificationJob BuildJob(bool ghlTestMode)
    {
        var enf = Options.Create(new EnforcementOptions()); // delay 10m ⇒ due 10m, hard grace 30m
        var safety = Options.Create(new SafetyOptions { GhlTestMode = ghlTestMode });
        return new GhlDeliveryVerificationJob(_db.Factory, _clock, enf, safety, NullLogger<GhlDeliveryVerificationJob>.Instance);
    }

    /// <summary>Client with an active GHL contact link, an open dunning case, and one
    /// triggered-but-unverified attempt at <paramref name="triggeredAt"/>.</summary>
    private (Guid ClientId, Guid AttemptId) SeedTriggeredAttempt(DateTimeOffset triggeredAt)
    {
        using var ctx = _db.CreateContext();
        var client = new Client
        {
            Id = Guid.NewGuid(), BusinessName = "Dunning Co",
            ContractType = ContractType.Paid, AccountType = AccountType.Master,
            EnforcementMode = EnforcementMode.Assist, CurrencyCode = "USD", CreatedAt = Now,
        };
        ctx.Clients.Add(client);
        ctx.IdentityLinks.Add(new IdentityLink
        {
            Id = Guid.NewGuid(), ClientId = client.Id, System = ExternalSystem.Ghl, Kind = LinkKind.Contact,
            ExternalId = ContactId, VerifiedAt = Now, CreatedAt = Now,
        });
        var dunningCase = new DunningCase
        {
            Id = Guid.NewGuid(), ClientId = client.Id, InvoiceExternalId = "in_f2",
            OpenedAt = Now.AddHours(-1), WindowExpiresAt = Now.AddHours(47), Status = DunningCaseStatus.Open,
        };
        ctx.DunningCases.Add(dunningCase);
        var attempt = new DunningAttempt
        {
            Id = Guid.NewGuid(), DunningCaseId = dunningCase.Id, Step = 1,
            DueAt = triggeredAt, TriggeredAt = triggeredAt, VerifiedAt = null,
        };
        ctx.DunningAttempts.Add(attempt);
        ctx.SaveChanges();
        return (client.Id, attempt.Id);
    }

    // ── An outbound message actually appeared for the contact ⇒ verified ──

    [Fact]
    public async Task Observed_outbound_message_marks_attempt_verified()
    {
        var triggeredAt = Now.AddMinutes(-20); // past due (10m), still inside hard grace (30m)
        var (clientId, attemptId) = SeedTriggeredAttempt(triggeredAt);
        using (var seed = _db.CreateContext())
        {
            seed.GhlMessages.Add(new GhlMessageProj
            {
                MessageId = "msg_1", ClientId = clientId, LocationId = "loc", ContactId = ContactId,
                MessageType = "SMS", SentAt = triggeredAt.AddMinutes(1), SourceSyncedAt = Now,
            });
            seed.SaveChanges();
        }

        await BuildJob(ghlTestMode: false).RunAsync(default);

        await using var ctx = _db.CreateContext();
        var attempt = await ctx.DunningAttempts.FindAsync(attemptId);
        attempt!.VerifiedAt.Should().Be(Now);          // verified at the job's clock-now — window may advance
        attempt.FailureReason.Should().BeNull();
        (await ctx.InvestigationItems.AnyAsync()).Should().BeFalse();
        (await ctx.AlertLog.AnyAsync()).Should().BeFalse();
    }

    // ── No message past the hard grace ⇒ escalate; the send stays unverified ──

    [Fact]
    public async Task No_message_past_hard_grace_escalates_and_never_verifies()
    {
        var triggeredAt = Now.AddMinutes(-40); // past the hard grace (3x delay = 30m), no message observed
        var (clientId, attemptId) = SeedTriggeredAttempt(triggeredAt);

        await BuildJob(ghlTestMode: false).RunAsync(default);

        await using var ctx = _db.CreateContext();
        var attempt = await ctx.DunningAttempts.FindAsync(attemptId);
        attempt!.VerifiedAt.Should().BeNull();          // never verified — the window must NOT advance
        attempt.FailureReason.Should().NotBeNullOrEmpty();

        var investigation = await ctx.InvestigationItems.SingleAsync();
        investigation.ClientId.Should().Be(clientId);
        investigation.Kind.Should().Be(InvestigationKind.DeliveryUnverified);
        investigation.Status.Should().Be(InvestigationStatus.Open);

        var alert = await ctx.AlertLog.SingleAsync();
        alert.Severity.Should().Be("High");
    }

    // ── Under GHL test mode the send was redirected, so there is no real delivery to confirm ⇒ verified (test) ──

    [Fact]
    public async Task Test_mode_auto_verifies_without_a_real_send()
    {
        var triggeredAt = Now.AddMinutes(-20);
        var (_, attemptId) = SeedTriggeredAttempt(triggeredAt);

        await BuildJob(ghlTestMode: true).RunAsync(default);

        await using var ctx = _db.CreateContext();
        var attempt = await ctx.DunningAttempts.FindAsync(attemptId);
        attempt!.VerifiedAt.Should().Be(Now);
        attempt.FailureReason.Should().Contain("test mode");
        (await ctx.InvestigationItems.AnyAsync()).Should().BeFalse();
        (await ctx.AlertLog.AnyAsync()).Should().BeFalse();
    }

    // ── Still inside the initial grace window ⇒ the job leaves it alone (checks again next pass) ──

    [Fact]
    public async Task Attempt_still_within_grace_is_left_untouched()
    {
        var triggeredAt = Now.AddMinutes(-5); // younger than the 10m delay — not yet due
        var (_, attemptId) = SeedTriggeredAttempt(triggeredAt);

        await BuildJob(ghlTestMode: false).RunAsync(default);

        await using var ctx = _db.CreateContext();
        var attempt = await ctx.DunningAttempts.FindAsync(attemptId);
        attempt!.VerifiedAt.Should().BeNull();          // not verified
        attempt.FailureReason.Should().BeNull();        // not escalated
        (await ctx.InvestigationItems.AnyAsync()).Should().BeFalse();
        (await ctx.AlertLog.AnyAsync()).Should().BeFalse();
    }

    public void Dispose() => _db.Dispose();
}
