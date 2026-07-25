using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RD.Domain;
using RD.Infrastructure;

namespace RD.Infrastructure.Sync;

/// <summary>
/// Reaps conversions that were billed but never paid. A conversion sits in AwaitingPayment with an
/// ExpiresAt; once that passes with no first payment, this sweep moves it to Expired so it drops out
/// of the "active conversion" set and surfaces as converted-but-unpaid for human follow-up.
///
/// Race-safe by construction: the set-based UPDATE carries a WHERE State = 'AwaitingPayment', so a
/// conversion the first-payment webhook promoted to Paid between reads is excluded by the predicate —
/// no row-version dance, no lost payment. A late payment AFTER expiry is recovered by the webhook
/// (Expired → Paid), so expiring here is never a dead end.
/// </summary>
public sealed class ConvertExpirySweepJob(
    IDbContextFactory<RdDbContext> dbFactory, IClock clock, ILogger<ConvertExpirySweepJob> logger)
{
    public async Task RunAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = clock.UtcNow;

        var expired = await db.ConvertIntents
            .Where(i => i.State == ConvertIntentState.AwaitingPayment && i.ExpiresAt != null && i.ExpiresAt <= now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.State, ConvertIntentState.Expired)
                .SetProperty(i => i.UpdatedAt, now), ct);

        if (expired > 0)
            logger.LogInformation("Convert expiry sweep expired {Count} unpaid conversion(s).", expired);
    }
}
