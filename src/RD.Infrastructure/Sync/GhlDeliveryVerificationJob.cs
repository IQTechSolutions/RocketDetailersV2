using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RD.Domain;
using RD.Domain.Entities;
using RD.Infrastructure.Enforcement;
using RD.Infrastructure.Gateways;

namespace RD.Infrastructure.Sync;

/// <summary>
/// F2: a GHL workflow trigger returning 200 proves nothing about delivery.
/// After a grace delay, confirm an outbound message actually appeared for the
/// contact (via the GhlMessages projection the sync populates). Verified ⇒ the
/// dunning window may advance past this step. Unverified past a hard grace ⇒
/// alert + work-queue item, and the window does NOT advance — a client is never
/// final-failed off warnings that never arrived.
///
/// Under Safety:GhlTestMode the send was redirected to the test contact, so
/// there is no real-client delivery to confirm; the attempt is marked verified
/// (test) so the end-to-end window logic stays exercisable in staging. The
/// production F2 guarantee applies only when TestMode is off.
/// </summary>
public sealed class GhlDeliveryVerificationJob(
    IDbContextFactory<RdDbContext> dbFactory,
    IClock clock,
    IOptions<EnforcementOptions> enforcement,
    IOptions<SafetyOptions> safety,
    ILogger<GhlDeliveryVerificationJob> logger)
{
    private readonly EnforcementOptions _enf = enforcement.Value;
    private readonly SafetyOptions _safety = safety.Value;

    public async Task RunAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = clock.UtcNow;
        var due = now - _enf.DeliveryVerificationDelay;
        var hardGrace = now - _enf.DeliveryVerificationDelay * 3;

        var pending = await (from a in db.DunningAttempts
                             where a.TriggeredAt != null && a.VerifiedAt == null && a.TriggeredAt <= due
                             join c in db.DunningCases on a.DunningCaseId equals c.Id
                             select new { Attempt = a, c.ClientId }).ToListAsync(ct);
        if (pending.Count == 0) return;

        var verified = 0; var escalated = 0;

        foreach (var item in pending)
        {
            var attempt = item.Attempt;

            if (_safety.GhlTestMode)
            {
                attempt.VerifiedAt = now;
                attempt.FailureReason = "verified (test mode) — send was redirected to the test contact";
                verified++;
                continue;
            }

            var contactId = await db.IdentityLinks.AsNoTracking()
                .Where(l => l.ClientId == item.ClientId && l.System == ExternalSystem.Ghl
                            && l.Kind == LinkKind.Contact && l.InvalidatedAt == null)
                .Select(l => l.ExternalId).FirstOrDefaultAsync(ct);

            var delivered = contactId is not null && await db.GhlMessages.AsNoTracking()
                .AnyAsync(m => m.ContactId == contactId && m.SentAt >= attempt.TriggeredAt, ct);

            if (delivered)
            {
                attempt.VerifiedAt = now;
                verified++;
            }
            else if (attempt.TriggeredAt <= hardGrace)
            {
                attempt.FailureReason = "no outbound message observed after the grace window — GHL delivery may be broken";
                await AddOnceAsync(db, item.ClientId, InvestigationKind.DeliveryUnverified,
                    $"Dunning step {attempt.Step} triggered at {attempt.TriggeredAt:u} but no outbound GHL message was observed. The dunning window will NOT advance.", now, ct);
                db.AlertLog.Add(new AlertLogEntry
                {
                    Id = Guid.NewGuid(), Severity = "High",
                    Message = $"Dunning delivery unverified for client {item.ClientId}, step {attempt.Step}.",
                    CreatedAt = now,
                });
                escalated++;
            }
            // else: still inside grace — leave unverified, check again next pass.
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("GHL delivery verification: {Verified} verified, {Escalated} escalated as unverified.", verified, escalated);
    }

    private static async Task AddOnceAsync(RdDbContext db, Guid clientId, InvestigationKind kind, string detail, DateTimeOffset now, CancellationToken ct)
    {
        var exists = await db.InvestigationItems.AnyAsync(
            i => i.ClientId == clientId && i.Kind == kind && i.Status == InvestigationStatus.Open, ct);
        if (!exists)
            db.InvestigationItems.Add(new InvestigationItem { Id = Guid.NewGuid(), ClientId = clientId, Kind = kind, Detail = detail, CreatedAt = now });
    }
}
