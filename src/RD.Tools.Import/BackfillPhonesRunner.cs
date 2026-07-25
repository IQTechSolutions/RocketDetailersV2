using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using RD.Domain;
using RD.Infrastructure;

namespace RD.Tools.Import;

/// <summary>
/// Backfills phone numbers onto clients that have none, using EXACT-id matches only
/// (GHL contact / Stripe customer / Meta campaign — we KNOW it's the right client).
/// Phone source: the dedicated Phone field, else Ads Contact Number, else Ads Contact #.
/// Never touches clients that already have a phone; never uses fuzzy name matches for
/// a write. Reads are projections, writes are targeted ExecuteUpdate. Dry-run unless --commit.
///
///   dotnet run --project src/RD.Tools.Import -- backfill-phones [--commit]
/// </summary>
public static class BackfillPhonesRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        var commit = args.Any(a => a.Equals("--commit", StringComparison.OrdinalIgnoreCase));

        var config = new ConfigurationBuilder()
            .AddUserSecrets(Assembly.GetExecutingAssembly(), optional: true)
            .AddEnvironmentVariables().Build();
        var token = config["ClickUp:ApiToken"];
        if (string.IsNullOrWhiteSpace(token)) { Console.WriteLine("ERROR: set ClickUp:ApiToken."); return 1; }
        var conn = Environment.GetEnvironmentVariable("RD_CONN") ?? config.GetConnectionString("RocketDetailers");
        if (string.IsNullOrWhiteSpace(conn)) { Console.WriteLine("ERROR: set ConnectionStrings:RocketDetailers or RD_CONN."); return 1; }
        var listId = ArgValue(args, "--list") ?? config["ClickUp:ClientListId"] ?? ClickUpDiscoveryRunner.DefaultListId;

        using var http = ClickUpApi.CreateClient(token);
        var fields = await ClickUpApi.GetFieldsAsync(http, listId);
        if (fields is null) return 1;
        var tasks = await ClickUpApi.GetTasksAsync(http, listId, fields);

        var options = new DbContextOptionsBuilder<RdDbContext>().UseSqlServer(conn).AddInterceptors(new AppendOnlyInterceptor()).Options;
        await using var db = new RdDbContext(options);
        var ct = CancellationToken.None;

        var index = await ClickUpMatchIndex.BuildAsync(db, ct);
        var clients = await db.Clients.Select(c => new { c.Id, c.Phone, c.BusinessName }).ToListAsync(ct);
        var hasPhone = clients.Where(c => ClickUpMatchIndex.PhoneTail(c.Phone) is not null).Select(c => c.Id).ToHashSet();
        var nameById = clients.ToDictionary(c => c.Id, c => c.BusinessName);

        int exactClients = 0, alreadyHasPhone = 0, noClickUpPhone = 0;
        var processed = new HashSet<Guid>();
        var toSet = new Dictionary<Guid, string>();
        var sample = new List<string>();

        Console.WriteLine($"[backfill-phones] mode={(commit ? "COMMIT" : "DRY-RUN")}  list={listId}");
        Console.WriteLine($"[backfill-phones] {tasks.Count} tasks.\n");

        foreach (var t in tasks)
        {
            var (id, _, exact) = index.Resolve(t);
            if (!exact || id is not { } cid) continue;      // exact matches only — never fuzzy for a write
            if (!processed.Add(cid)) continue;              // one client once
            exactClients++;
            if (hasPhone.Contains(cid)) { alreadyHasPhone++; continue; }
            var phone = BestPhone(t);
            if (phone is null) { noClickUpPhone++; continue; }
            toSet[cid] = phone;
            if (sample.Count < 15) sample.Add($"{nameById[cid]} → {phone}");
        }

        Console.WriteLine($"[backfill-phones] exact-id-matched clients: {exactClients}");
        Console.WriteLine($"[backfill-phones]   already have a phone:      {alreadyHasPhone}");
        Console.WriteLine($"[backfill-phones]   no phone in ClickUp:       {noClickUpPhone}");
        Console.WriteLine($"[backfill-phones]   PHONES TO BACKFILL:        {toSet.Count}\n");
        if (sample.Count > 0)
        {
            foreach (var s in sample) Console.WriteLine($"    + {s}");
            if (toSet.Count > sample.Count) Console.WriteLine($"    … +{toSet.Count - sample.Count} more");
            Console.WriteLine();
        }

        if (commit)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            foreach (var (cid, phone) in toSet)
                await db.Clients.Where(c => c.Id == cid).ExecuteUpdateAsync(s => s.SetProperty(c => c.Phone, phone), ct);
            await tx.CommitAsync(ct);
            Console.WriteLine($"[backfill-phones] COMMITTED — {toSet.Count} phone numbers backfilled.");
        }
        else
        {
            Console.WriteLine("[backfill-phones] DRY-RUN — nothing written. Re-run with --commit to persist.");
        }
        return 0;
    }

    private static string? BestPhone(ClickUpTask t)
    {
        foreach (var name in new[] { "Phone", "Ads Contact Number", "Ads Contact #" })
        {
            var v = t.Field(name);
            if (v is not null && ClickUpMatchIndex.PhoneTail(v) is not null) return v.Trim();
        }
        return null;
    }

    private static string? ArgValue(string[] args, string flag)
    {
        var i = Array.FindIndex(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
