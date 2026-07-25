using System.Globalization;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using RD.Domain;
using RD.Infrastructure;

namespace RD.Tools.Import;

/// <summary>
/// READ-ONLY post-reconciliation scorecard, straight from the DB — client mix,
/// identity-link coverage, phone coverage, open investigations, and the money
/// picture (charges in, ad spend out, net cash) the analytics dashboard computes.
/// Uses projections only, so it works even when the app can't (unapplied ClientMerge
/// migration). Writes nothing.
///
///   dotnet run --project src/RD.Tools.Import -- scorecard
/// </summary>
public static class ScorecardRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets(Assembly.GetExecutingAssembly(), optional: true)
            .AddEnvironmentVariables().Build();
        var conn = Environment.GetEnvironmentVariable("RD_CONN") ?? config.GetConnectionString("RocketDetailers");
        if (string.IsNullOrWhiteSpace(conn)) { Console.WriteLine("ERROR: set ConnectionStrings:RocketDetailers or RD_CONN."); return 1; }

        var options = new DbContextOptionsBuilder<RdDbContext>().UseSqlServer(conn).Options;
        await using var db = new RdDbContext(options);
        var ct = CancellationToken.None;

        // Client mix (avoid MergedIntoClientId — that column isn't on the DB yet).
        var clientMix = await db.Clients
            .GroupBy(c => new { c.AccountType, c.ContractType })
            .Select(g => new { g.Key.AccountType, g.Key.ContractType, Count = g.Count() })
            .ToListAsync(ct);
        var totalClients = clientMix.Sum(x => x.Count);
        var trials = clientMix.Where(x => x.ContractType == ContractType.Trial).Sum(x => x.Count);
        var masters = clientMix.Where(x => x.AccountType == AccountType.Master).Sum(x => x.Count);
        var withPhone = await db.Clients.CountAsync(c => c.Phone != null && c.Phone != "", ct);
        var trialPeriods = await db.TrialPeriods.CountAsync(ct);

        // Identity links by system+kind (active).
        var links = await db.IdentityLinks.AsNoTracking()
            .Where(l => l.InvalidatedAt == null)
            .GroupBy(l => new { l.System, l.Kind })
            .Select(g => new { g.Key.System, g.Key.Kind, Count = g.Count() })
            .ToListAsync(ct);

        // Money picture for master clients: charges in + ad spend out → net cash.
        var money = await db.LedgerEntries.AsNoTracking()
            .Where(l => l.Type == LedgerEntryType.ChargePaid || l.Type == LedgerEntryType.AdSpend)
            .Join(db.Clients.Where(c => c.AccountType == AccountType.Master), l => l.ClientId, c => c.Id, (l, c) => l)
            .GroupBy(l => l.Type)
            .Select(g => new { Type = g.Key, Total = g.Sum(x => x.SignedAmount), Count = g.Count() })
            .ToListAsync(ct);
        var chargesIn = money.Where(m => m.Type == LedgerEntryType.ChargePaid).Sum(m => m.Total);
        var adSpendOut = money.Where(m => m.Type == LedgerEntryType.AdSpend).Sum(m => m.Total); // negative
        var adSpendRows = money.Where(m => m.Type == LedgerEntryType.AdSpend).Sum(m => m.Count);

        // Open investigations by kind.
        var invs = await db.InvestigationItems.AsNoTracking()
            .Where(i => i.Status == InvestigationStatus.Open)
            .GroupBy(i => i.Kind).Select(g => new { Kind = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        Console.WriteLine("=== Reconciliation scorecard ===\n");
        Console.WriteLine($"Clients: {totalClients}   (master: {masters}, trials: {trials}, with phone: {withPhone})   trial periods: {trialPeriods}\n");

        Console.WriteLine("Active identity links:");
        foreach (var g in links.OrderBy(l => l.System.ToString()).ThenBy(l => l.Kind.ToString()))
            Console.WriteLine($"  {g.System}/{g.Kind}: {g.Count}");
        Console.WriteLine();

        Console.WriteLine("Money (master clients) — what the net-cash dashboard reads:");
        Console.WriteLine($"  charges in:   {Money(chargesIn)}");
        Console.WriteLine($"  ad spend out: {Money(adSpendOut)}   ({adSpendRows} AdSpend rows)");
        Console.WriteLine($"  NET CASH:     {Money(chargesIn + adSpendOut)}\n");

        Console.WriteLine("Open investigations:");
        foreach (var g in invs.OrderByDescending(i => i.Count))
            Console.WriteLine($"  {g.Kind}: {g.Count}");
        Console.WriteLine($"  total: {invs.Sum(i => i.Count)}");
        return 0;
    }

    private static string Money(decimal v) => "$" + v.ToString("N2", CultureInfo.InvariantCulture);
}
