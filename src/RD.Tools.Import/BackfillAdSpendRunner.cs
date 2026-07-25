using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RD.Domain;
using RD.Domain.Entities;
using RD.Infrastructure;
using RD.Infrastructure.Gateways;
using RD.Infrastructure.Sync;

namespace RD.Tools.Import;

/// <summary>
/// One-off Meta ad-spend backfill into the append-only ledger, so net-cash / exposure
/// reflects the campaigns we just linked. Reads historical daily insights for the
/// master ad account, attributes each campaign-day to its linked client, and inserts
/// the AdSpend entries that aren't already in the ledger (idempotency key
/// campaignId:date — money is never double-counted). Read-only against Meta; the
/// only writes are new ledger rows. Dry-run unless --commit.
///
///   dotnet run --project src/RD.Tools.Import -- backfill-adspend [--days N] [--commit]
///
/// Needs Meta:AccessToken + Meta:AdAccountId in user-secrets (never committed) and a DB connection.
/// </summary>
public static class BackfillAdSpendRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        var commit = args.Any(a => a.Equals("--commit", StringComparison.OrdinalIgnoreCase));
        var days = int.TryParse(ArgValue(args, "--days"), out var d) && d > 0 ? d : 365;

        var config = new ConfigurationBuilder()
            .AddUserSecrets(Assembly.GetExecutingAssembly(), optional: true)
            .AddEnvironmentVariables().Build();
        var conn = Environment.GetEnvironmentVariable("RD_CONN") ?? config.GetConnectionString("RocketDetailers");
        if (string.IsNullOrWhiteSpace(conn)) { Console.WriteLine("ERROR: set ConnectionStrings:RocketDetailers or RD_CONN."); return 1; }

        var services = new ServiceCollection();
        services.AddLogging(l => l.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IConfiguration>(config);
        services.AddDbContextFactory<RdDbContext>(o => o.UseSqlServer(conn).AddInterceptors(new AppendOnlyInterceptor()));
        services.AddRdSync(config);
        await using var provider = services.BuildServiceProvider();

        var metaOpts = provider.GetRequiredService<IOptions<MetaOptions>>().Value;
        if (string.IsNullOrWhiteSpace(metaOpts.AccessToken))
        {
            Console.WriteLine("ERROR: no Meta token. Set it (never committed):");
            Console.WriteLine("  dotnet user-secrets set \"Meta:AccessToken\" \"<token>\" --project src/RD.Tools.Import");
            return 1;
        }
        if (string.IsNullOrWhiteSpace(metaOpts.AdAccountId))
        {
            Console.WriteLine("ERROR: set Meta:AdAccountId (the master ad account).");
            return 1;
        }

        var meta = provider.GetRequiredService<IMetaAdsGateway>();
        var factory = provider.GetRequiredService<IDbContextFactory<RdDbContext>>();
        var ct = CancellationToken.None;
        var now = DateTimeOffset.UtcNow;
        var today = DateOnly.FromDateTime(now.UtcDateTime.Date);
        var since = today.AddDays(-days);

        await using var db = await factory.CreateDbContextAsync(ct);

        var campaignToClient = (await db.IdentityLinks.AsNoTracking()
                .Where(l => l.System == ExternalSystem.Meta && l.Kind == LinkKind.Campaign && l.InvalidatedAt == null)
                .Select(l => new { l.ExternalId, l.ClientId }).ToListAsync(ct))
            .ToDictionary(x => x.ExternalId, x => x.ClientId);

        Console.WriteLine($"[backfill-adspend] mode={(commit ? "COMMIT" : "DRY-RUN")}  ad account {metaOpts.AdAccountId}");
        Console.WriteLine($"[backfill-adspend] fetching daily insights {since:yyyy-MM-dd} .. {today:yyyy-MM-dd} ({days} days)…");
        var insights = await meta.GetDailyInsightsAsync(metaOpts.AdAccountId, since, today, ct);
        Console.WriteLine($"[backfill-adspend] {insights.Count} campaign-day rows returned.\n");

        var sinceDto = new DateTimeOffset(since.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var existing = (await db.LedgerEntries.AsNoTracking()
                .Where(l => l.Type == LedgerEntryType.AdSpend && l.SourceSystem == ExternalSystem.Meta && l.OccurredAt >= sinceDto)
                .Select(l => l.SourceObjectId).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        int spendDays = 0, linkedDays = 0, unlinkedDays = 0, alreadyLedgered = 0, newDays = 0;
        decimal totalSpend = 0m, linkedSpend = 0m, newSpend = 0m;
        var newByClient = new Dictionary<Guid, decimal>();
        var candidates = new List<LedgerEntry>();

        foreach (var i in insights)
        {
            if (i.Spend <= 0m) continue;
            spendDays++; totalSpend += i.Spend;
            if (!campaignToClient.TryGetValue(i.CampaignId, out var cid)) { unlinkedDays++; continue; }
            linkedDays++; linkedSpend += i.Spend;

            var soid = $"{i.CampaignId}:{i.Date:yyyy-MM-dd}";
            if (existing.Contains(soid)) { alreadyLedgered++; continue; }
            newDays++; newSpend += i.Spend;
            newByClient[cid] = newByClient.GetValueOrDefault(cid) + i.Spend;
            candidates.Add(new LedgerEntry
            {
                ClientId = cid,
                OccurredAt = new DateTimeOffset(i.Date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
                RecordedAt = now,
                Type = LedgerEntryType.AdSpend,
                SignedAmount = -i.Spend, // money out → negative
                CurrencyCode = metaOpts.AccountCurrency,
                SourceSystem = ExternalSystem.Meta,
                SourceObjectId = soid,
            });
        }

        var nameById = (await db.Clients.AsNoTracking()
                .Where(c => newByClient.Keys.Contains(c.Id))
                .Select(c => new { c.Id, c.BusinessName }).ToListAsync(ct))
            .ToDictionary(c => c.Id, c => c.BusinessName);

        Console.WriteLine("=== Ad-spend in window ===");
        Console.WriteLine($"  campaign-days with spend: {spendDays}   total spend: {Money(totalSpend)}");
        Console.WriteLine($"  attributable (campaign linked to a client): {linkedDays} days, {Money(linkedSpend)}");
        Console.WriteLine($"  unattributable (campaign not linked):       {unlinkedDays} days\n");
        Console.WriteLine("=== Ledger impact ===");
        Console.WriteLine($"  already in the ledger (skip):  {alreadyLedgered} days");
        Console.WriteLine($"  NEW AdSpend rows to add:       {newDays} days, {Money(newSpend)}");
        Console.WriteLine($"  clients affected:              {newByClient.Count}\n");
        if (newByClient.Count > 0)
        {
            Console.WriteLine("  Top clients by new ad-spend to ledger:");
            foreach (var kv in newByClient.OrderByDescending(kv => kv.Value).Take(15))
                Console.WriteLine($"    - {nameById.GetValueOrDefault(kv.Key, "?")}: {Money(kv.Value)}");
            Console.WriteLine();
        }

        if (commit)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            db.LedgerEntries.AddRange(candidates);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex)
            {
                await tx.RollbackAsync(ct);
                Console.WriteLine($"[backfill-adspend] insert conflict (a concurrent sync may have raced): {ex.GetBaseException().Message}");
                return 1;
            }
            await tx.CommitAsync(ct);
            Console.WriteLine($"[backfill-adspend] COMMITTED — {newDays} AdSpend rows, {Money(newSpend)} total, across {newByClient.Count} clients.");
        }
        else
        {
            Console.WriteLine("[backfill-adspend] DRY-RUN — nothing written. Re-run with --commit to persist.");
        }
        return 0;
    }

    private static string Money(decimal v) => $"${v:N2}";

    private static string? ArgValue(string[] args, string flag)
    {
        var i = Array.FindIndex(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
