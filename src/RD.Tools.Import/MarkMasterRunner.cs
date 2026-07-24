using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using RD.Domain;
using RD.Infrastructure;

namespace RD.Tools.Import;

/// <summary>
/// Sets AccountType = Master for the clients the master sheet marks
/// "Master Ad Account? = Yes" (bridged by Stripe customer id).
///
///   dotnet run --project src/RD.Tools.Import -- mark-master [--commit] [--crosswalk &lt;xlsx&gt;]
///
/// The policy heartbeat only evaluates Master-account clients — the ones whose
/// ads run under the shared master account and can actually be enforced — so
/// until the reconciled clients are marked, evaluation produces nothing. This
/// puts them in scope. Own-account clients stay Own (their ads aren't ours to
/// pause). Dry-run by default; --commit to persist. Connection: RD_CONN.
/// </summary>
public static class MarkMasterRunner
{
    private const string DefaultCrosswalkName = "All Clients - completed Final.xlsx";

    public static async Task<int> RunAsync(string[] args)
    {
        var commit = args.Any(a => a.Equals("--commit", StringComparison.OrdinalIgnoreCase));
        var crosswalkPath = ResolveCrosswalkPath(args);

        var conn = Environment.GetEnvironmentVariable("RD_CONN");
        if (string.IsNullOrWhiteSpace(conn)) { Console.WriteLine("ERROR: set the RD_CONN environment variable."); return 1; }
        if (crosswalkPath is null) { Console.WriteLine($"ERROR: crosswalk sheet not found. Pass --crosswalk <xlsx> or place '{DefaultCrosswalkName}' in Downloads."); return 1; }

        Console.WriteLine($"[mark-master] mode={(commit ? "COMMIT" : "DRY-RUN")}");
        var masterCustomers = LoadMasterCustomers(crosswalkPath);
        Console.WriteLine($"[mark-master] sheet marks {masterCustomers.Count} Stripe customers as Master-account.");
        if (masterCustomers.Count == 0) { Console.WriteLine("[mark-master] nothing to mark (need 'Master Ad Account?' + 'Stripe Customer ID' columns)."); return 1; }

        var options = new DbContextOptionsBuilder<RdDbContext>().UseSqlServer(conn)
            .AddInterceptors(new AppendOnlyInterceptor()).Options;
        await using var db = new RdDbContext(options);
        var ct = CancellationToken.None;

        var custLinks = await db.IdentityLinks.AsNoTracking()
            .Where(l => l.System == ExternalSystem.Stripe && l.Kind == LinkKind.Customer && l.InvalidatedAt == null)
            .Select(l => new { l.ClientId, l.ExternalId })
            .ToListAsync(ct);

        var masterClientIds = custLinks
            .Where(l => masterCustomers.Contains(l.ExternalId))
            .Select(l => l.ClientId).Distinct().ToList();

        var alreadyMaster = await db.Clients.AsNoTracking()
            .Where(c => masterClientIds.Contains(c.Id) && c.AccountType == AccountType.Master).CountAsync(ct);
        var toFlip = masterClientIds.Count - alreadyMaster;

        Console.WriteLine($"[mark-master] {masterClientIds.Count} clients match a Master-account Stripe customer ({alreadyMaster} already Master, {toFlip} to flip).");

        if (commit)
        {
            // Bulk update (bypasses the append-only SaveChanges interceptor, like the policy job's demote).
            var flipped = await db.Clients
                .Where(c => masterClientIds.Contains(c.Id) && c.AccountType != AccountType.Master)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.AccountType, AccountType.Master), ct);
            Console.WriteLine($"[mark-master] COMMITTED — set {flipped} clients to AccountType=Master (now in the policy's evaluation scope).");
        }
        else
        {
            Console.WriteLine("[mark-master] DRY-RUN — nothing written. Re-run with --commit to persist.");
        }
        return 0;
    }

    private static string? ResolveCrosswalkPath(string[] args)
    {
        var idx = Array.FindIndex(args, a => a.Equals("--crosswalk", StringComparison.OrdinalIgnoreCase));
        if (idx >= 0 && idx + 1 < args.Length)
            return File.Exists(args[idx + 1]) ? args[idx + 1] : null;
        var def = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", DefaultCrosswalkName);
        return File.Exists(def) ? def : null;
    }

    /// <summary>Stripe customer ids whose sheet row says "Master Ad Account? = Yes".</summary>
    private static HashSet<string> LoadMasterCustomers(string path)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var wb = new XLWorkbook(path);
            var ws = wb.Worksheet("All Clients");
            var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var cell in ws.Row(4).CellsUsed()) headers[cell.GetString().Trim()] = cell.Address.ColumnNumber;
            int? Col(string sub) => headers.Where(kv => kv.Key.Contains(sub, StringComparison.OrdinalIgnoreCase))
                .Select(kv => (int?)kv.Value).FirstOrDefault();

            var masterCol = Col("Master Ad Account");
            var idCols = new[] { Col("Stripe Customer ID (cus"), Col("Stripe Customer ID 2") }.Where(c => c is not null).ToList();
            if (masterCol is null || idCols.Count == 0) return set;

            var last = ws.LastRowUsed()!.RowNumber();
            for (var r = 5; r <= last; r++)
            {
                if (!ws.Row(r).Cell(masterCol.Value).GetString().Trim().Equals("Yes", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var c in idCols)
                {
                    var cid = ws.Row(r).Cell(c!.Value).GetString().Trim();
                    if (cid.StartsWith("cus_", StringComparison.OrdinalIgnoreCase)) set.Add(cid);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[mark-master] crosswalk load failed ({ex.GetType().Name}: {ex.Message}).");
        }
        return set;
    }
}
