using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using RD.Domain;
using RD.Infrastructure;

namespace RD.Tools.Import;

/// <summary>
/// READ-ONLY ClickUp discovery probe — the "discover-first" step before the trial
/// importer. Prints the Client Database list's custom-field catalog, counts tasks
/// by Contract (Trial/Paid) and by Status, dumps a few sample tasks, and — if a DB
/// connection is available — cross-checks the trial tasks against existing clients
/// (by normalized name) to show how many are missing. Writes NOTHING.
///
///   dotnet run --project src/RD.Tools.Import -- discover-clickup [--list &lt;id&gt;] [--sample N]
///
/// Auth (never committed):
///   dotnet user-secrets set "ClickUp:ApiToken" "pk_..." --project src/RD.Tools.Import
/// </summary>
public static class ClickUpDiscoveryRunner
{
    public const string DefaultListId = "901108300071"; // from the Client Database URL (…/li/6-901108300071-1)

    public static async Task<int> RunAsync(string[] args)
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets(Assembly.GetExecutingAssembly(), optional: true)
            .AddEnvironmentVariables()
            .Build();

        var token = config["ClickUp:ApiToken"];
        if (string.IsNullOrWhiteSpace(token))
        {
            Console.WriteLine("ERROR: no ClickUp token. Set it (never committed):");
            Console.WriteLine("  dotnet user-secrets set \"ClickUp:ApiToken\" \"pk_...\" --project src/RD.Tools.Import");
            return 1;
        }

        var listId = ArgValue(args, "--list") ?? config["ClickUp:ClientListId"] ?? DefaultListId;
        var sample = int.TryParse(ArgValue(args, "--sample"), out var s) ? s : 5;

        using var http = ClickUpApi.CreateClient(token);
        Console.WriteLine($"[discover-clickup] list={listId}  (read-only)\n");

        var fields = await ClickUpApi.GetFieldsAsync(http, listId);
        if (fields is null) return 1;

        Console.WriteLine($"=== Custom fields ({fields.Count}) ===");
        foreach (var f in fields.Values.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            var opts = f.OptionsByKey.Count > 0 ? $"  options: {string.Join(", ", f.OptionsByKey.Values.Distinct())}" : "";
            Console.WriteLine($"  • {f.Name}  [{f.Type}]  id={f.Id}{opts}");
        }
        Console.WriteLine();

        var tasks = await ClickUpApi.GetTasksAsync(http, listId, fields);
        Console.WriteLine($"=== Tasks: {tasks.Count} pulled (include_closed, non-archived) ===\n");

        Console.WriteLine("=== Contract breakdown ===");
        foreach (var g in tasks.GroupBy(t => t.Field("Contract") ?? "(blank)").OrderByDescending(g => g.Count()))
            Console.WriteLine($"  {g.Key,-20} {g.Count()}");
        Console.WriteLine();

        Console.WriteLine("=== Status (custom field) breakdown ===");
        foreach (var g in tasks.GroupBy(t => t.Field("Status") ?? "(blank)").OrderByDescending(g => g.Count()))
            Console.WriteLine($"  {g.Key,-40} {g.Count()}");
        Console.WriteLine();

        Console.WriteLine($"=== Sample tasks (first {sample}) ===");
        foreach (var t in tasks.Take(sample))
        {
            Console.WriteLine($"  ── {t.Name}   (workflow={t.Status}, id={t.Id})");
            foreach (var (name, val) in t.Fields.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                if (!string.IsNullOrWhiteSpace(val)) Console.WriteLine($"        {name}: {val}");
        }
        Console.WriteLine();

        var trials = tasks.Where(t => string.Equals(t.Field("Contract"), "Trial", StringComparison.OrdinalIgnoreCase)).ToList();
        await CrossCheckMissingAsync(config, trials);

        Console.WriteLine("\n[discover-clickup] done — read-only, nothing written.");
        return 0;
    }

    private static async Task CrossCheckMissingAsync(IConfiguration config, List<ClickUpTask> trials)
    {
        var conn = Environment.GetEnvironmentVariable("RD_CONN") ?? config.GetConnectionString("RocketDetailers");
        if (string.IsNullOrWhiteSpace(conn))
        {
            Console.WriteLine($"=== Trials in ClickUp: {trials.Count} (set RD_CONN or a connection string to cross-check against the DB) ===");
            return;
        }

        try
        {
            var options = new DbContextOptionsBuilder<RdDbContext>().UseSqlServer(conn).Options;
            await using var db = new RdDbContext(options);

            var existing = await db.Clients.AsNoTracking().Select(c => new { c.BusinessName, c.ContactName }).ToListAsync();
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in existing)
            {
                if (NameNormalizer.Normalize(c.BusinessName) is { Length: > 0 } b) known.Add(b);
                if (c.ContactName is { } cn && NameNormalizer.Normalize(cn) is { Length: > 0 } n) known.Add(n);
            }

            // A ClickUp trial matches if EITHER its business name field OR its task (person) name is already known.
            bool Present(ClickUpTask t) =>
                known.Contains(NameNormalizer.Normalize(t.Field("Business Name")))
                || known.Contains(NameNormalizer.Normalize(t.Name));

            var missing = trials.Where(t => !Present(t)).ToList();
            Console.WriteLine("=== Trial cross-check (all statuses) ===");
            Console.WriteLine($"  ClickUp trials:        {trials.Count}");
            Console.WriteLine($"  already in the app:    {trials.Count - missing.Count}");
            Console.WriteLine($"  MISSING (all trials):  {missing.Count}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"(DB cross-check skipped: {ex.Message})");
        }
    }

    private static string? ArgValue(string[] args, string flag)
    {
        var i = Array.FindIndex(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
