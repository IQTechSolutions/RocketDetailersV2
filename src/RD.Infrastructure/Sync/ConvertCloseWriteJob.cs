using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RD.Domain;
using RD.Infrastructure.Enforcement;
using RD.Infrastructure.Gateways;

namespace RD.Infrastructure.Sync;

/// <summary>
/// The final step of Convert→Bill→Close: write the `close` tag on the GHL contact of a paid
/// conversion, which detonates the trusted downstream onboarding chain (welcome SMS + sub-account,
/// run in Zapier). Deferred to a job — never inline in the webhook transaction — so a GHL outage
/// can't poison the payment event.
///
/// Safety, all default-closed:
///   1. Convert:CloseTagWriteEnabled must be true (default false) — off, this job no-ops.
///   2. The GHL gateway's TestMode (default ON) redirects the write to the test contact regardless.
///   3. The global kill switch halts it.
///   4. Read-before-write is MANDATORY (GHL doesn't guarantee re-adding a tag is a no-op): GET the
///      contact's tags and skip the POST if `close` is already present, so a retry can't double-fire
///      the onboarding chain.
///
/// On a completed write the conversion moves Paid → Closed. Per-intent saves so one contact's failure
/// doesn't stall the batch.
/// </summary>
public sealed class ConvertCloseWriteJob(
    IDbContextFactory<RdDbContext> dbFactory,
    IGhlGateway ghl,
    KillSwitchService killSwitch,
    IOptions<ConvertOptions> convertOptions,
    IOptions<EnforcementOptions> enforcement,
    IClock clock,
    ILogger<ConvertCloseWriteJob> logger)
{
    private const string CloseTag = "close";

    public async Task RunAsync(CancellationToken ct)
    {
        if (!convertOptions.Value.CloseTagWriteEnabled) return;          // gate 1: feature off
        if (await killSwitch.IsEngagedAsync(ct)) return;                 // gate 3: global stop

        var locationId = enforcement.Value.DefaultDunningLocationId;     // client-contact location (shared w/ dunning; moot under TestMode)

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = clock.UtcNow;

        var pending = await db.ConvertIntents
            .Where(i => i.State == ConvertIntentState.Paid
                        && i.CloseTagWrittenAt == null
                        && i.CloseTagContactId != null)
            .OrderBy(i => i.UpdatedAt)
            .Take(50)
            .ToListAsync(ct);

        var written = 0;
        foreach (var intent in pending)
        {
            var contactId = intent.CloseTagContactId!;
            try
            {
                // Read-before-write: never re-add an already-present tag (would re-fire onboarding).
                var tags = await ghl.GetContactTagsAsync(locationId, contactId, ct);
                if (!tags.Contains(CloseTag, StringComparer.OrdinalIgnoreCase))
                    await ghl.AddContactTagAsync(locationId, contactId, CloseTag, ct);

                intent.CloseTagWrittenAt = now;
                intent.State = ConvertIntentState.Closed;
                intent.UpdatedAt = now;
                await db.SaveChangesAsync(ct);
                written++;
            }
            catch (Exception ex)
            {
                // Leave the intent Paid; the next pass retries. One bad contact must not stall the rest.
                logger.LogError(ex, "close-write failed for conversion {IntentId} (contact {ContactId})", intent.Id, contactId);
                db.ChangeTracker.Clear();
            }
        }

        if (written > 0) logger.LogInformation("Convert close-write tagged {Count} conversion(s) `close`.", written);
    }
}
