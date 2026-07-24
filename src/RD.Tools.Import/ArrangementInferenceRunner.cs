using Microsoft.EntityFrameworkCore;
using RD.Domain;
using RD.Domain.Entities;
using RD.Infrastructure;

namespace RD.Tools.Import;

/// <summary>
/// Infers each client's payment arrangement (typical amount + cadence) from their
/// SUBSCRIPTION payment history — the recurring commitment — using
/// <see cref="ArrangementInference"/>. Confident, regular cadences are set as
/// Inferred; everything else (irregular, too few payments, direct-only payers)
/// becomes NeedsReview + a "confirm arrangement" investigation for a human.
/// Human-Confirmed arrangements are never overwritten.
///
///   dotnet run --project src/RD.Tools.Import -- infer-arrangements [--commit]
///
/// Idempotent; dry-run by default. Connection: RD_CONN.
/// </summary>
public static class ArrangementInferenceRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        var commit = args.Any(a => a.Equals("--commit", StringComparison.OrdinalIgnoreCase));
        var conn = Environment.GetEnvironmentVariable("RD_CONN");
        if (string.IsNullOrWhiteSpace(conn)) { Console.WriteLine("ERROR: set the RD_CONN environment variable."); return 1; }

        var options = new DbContextOptionsBuilder<RdDbContext>().UseSqlServer(conn)
            .AddInterceptors(new AppendOnlyInterceptor()).Options;
        await using var db = new RdDbContext(options);
        var ct = CancellationToken.None;

        Console.WriteLine($"[infer-arrangements] mode={(commit ? "COMMIT" : "DRY-RUN")}");

        // All client payments (small); split subscription (in_) from direct.
        var pays = await db.LedgerEntries.AsNoTracking()
            .Where(l => l.Type == LedgerEntryType.ChargePaid)
            .Select(l => new { l.ClientId, l.OccurredAt, l.SignedAmount, l.SourceObjectId })
            .ToListAsync(ct);
        var byClient = pays.GroupBy(p => p.ClientId).ToDictionary(g => g.Key, g => g.ToList());

        var existingFlags = (await db.InvestigationItems.AsNoTracking()
                .Where(i => i.Status == InvestigationStatus.Open && i.Kind == InvestigationKind.PaymentArrangementUnclear)
                .Select(i => i.ClientId).ToListAsync(ct))
            .Where(id => id != null).Select(id => id!.Value).ToHashSet();

        var clients = await db.Clients.ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        int inferred = 0, needsReview = 0, unknown = 0, skippedConfirmed = 0, flags = 0;
        var cadenceCounts = new Dictionary<int, int>();

        foreach (var client in clients)
        {
            if (client.ArrangementStatus == ArrangementStatus.Confirmed) { skippedConfirmed++; continue; }

            var clientPays = byClient.TryGetValue(client.Id, out var ps) ? ps : [];
            var subPays = clientPays
                .Where(p => p.SourceObjectId.StartsWith("in_", StringComparison.OrdinalIgnoreCase))
                .Select(p => (p.OccurredAt, p.SignedAmount))
                .ToList();

            var result = ArrangementInference.Infer(subPays);
            var status = result.Status;
            // A direct-only payer (pays, but no clean subscription cadence) still needs a human.
            if (status == ArrangementStatus.Unknown && clientPays.Count > 0) status = ArrangementStatus.NeedsReview;

            client.ExpectedAmount = result.ExpectedAmount;
            client.CadenceDays = result.CadenceDays;
            client.ArrangementStatus = status;

            switch (status)
            {
                case ArrangementStatus.Inferred:
                    inferred++;
                    if (result.CadenceDays is { } cd) cadenceCounts[cd] = cadenceCounts.GetValueOrDefault(cd) + 1;
                    break;
                case ArrangementStatus.NeedsReview:
                    needsReview++;
                    if (existingFlags.Add(client.Id))
                    {
                        var amt = result.ExpectedAmount is { } a ? $"~${a:0.##}" : "unknown amount";
                        var cad = result.CadenceDays is { } c ? $"every {c}d" : "no clear cadence";
                        db.InvestigationItems.Add(new InvestigationItem
                        {
                            Id = Guid.NewGuid(), ClientId = client.Id, Kind = InvestigationKind.PaymentArrangementUnclear,
                            Detail = $"Confirm payment arrangement — inferred {amt} {cad} from {subPays.Count} subscription payment(s) (low confidence). Set the negotiated terms.",
                            CreatedAt = now,
                        });
                        flags++;
                    }
                    break;
                default:
                    unknown++;
                    break;
            }
        }

        var cadenceSummary = string.Join(", ", cadenceCounts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Value}×{kv.Key}d"));
        Console.WriteLine($"[infer-arrangements] inferred (confident): {inferred}  [{cadenceSummary}]");
        Console.WriteLine($"[infer-arrangements] needs-review (human): {needsReview}  ({flags} new investigation items)");
        Console.WriteLine($"[infer-arrangements] no payments yet (unknown): {unknown}.  already human-confirmed (skipped): {skippedConfirmed}.");

        if (commit)
        {
            await db.SaveChangesAsync(ct);
            Console.WriteLine("[infer-arrangements] COMMITTED.");
        }
        else
        {
            Console.WriteLine("[infer-arrangements] DRY-RUN — nothing written. Re-run with --commit to persist.");
        }
        return 0;
    }
}
