using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using RD.Domain;
using RD.Domain.Entities;
using RD.Infrastructure;

namespace RD.Tools.Import;

/// <summary>
/// MATCH-AND-MARK, never import. ClickUp's active-trial cohort is matched against
/// the clients ALREADY in the app — by email, then phone, then normalized name —
/// and each unambiguous match that isn't already a trial is flipped to
/// ContractType.Trial (and given a TrialPeriod if it has none). No new clients are
/// ever created; ClickUp trials with no match in the app are just reported.
///
/// Reads are projections and writes are a targeted ExecuteUpdate, so the tool
/// only ever touches the columns it changes (Contract) — it is unaffected by
/// unrelated schema the DB may be behind on.
///
///   dotnet run --project src/RD.Tools.Import -- mark-trials [--list &lt;id&gt;] [--commit] [--no-trial-period]
///
/// Requires (never committed): ClickUp:ApiToken and a DB connection
/// (RD_CONN env var or ConnectionStrings:RocketDetailers in user-secrets).
/// </summary>
public static class ClickUpTrialImportRunner
{
    private static readonly HashSet<string> ActiveTrialStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Interview Booked", "RedZone (>80%)", "First Job Booked", "Live + Onboarded", "Ads Are Live", "New / Open",
    };

    public static async Task<int> RunAsync(string[] args)
    {
        var commit = args.Any(a => a.Equals("--commit", StringComparison.OrdinalIgnoreCase));
        var noTrialPeriod = args.Any(a => a.Equals("--no-trial-period", StringComparison.OrdinalIgnoreCase));

        var config = new ConfigurationBuilder()
            .AddUserSecrets(Assembly.GetExecutingAssembly(), optional: true)
            .AddEnvironmentVariables()
            .Build();

        var token = config["ClickUp:ApiToken"];
        if (string.IsNullOrWhiteSpace(token))
        {
            Console.WriteLine("ERROR: no ClickUp token. Set it: dotnet user-secrets set \"ClickUp:ApiToken\" \"pk_...\" --project src/RD.Tools.Import");
            return 1;
        }
        var conn = Environment.GetEnvironmentVariable("RD_CONN") ?? config.GetConnectionString("RocketDetailers");
        if (string.IsNullOrWhiteSpace(conn))
        {
            Console.WriteLine("ERROR: no DB connection. Set ConnectionStrings:RocketDetailers in user-secrets or export RD_CONN.");
            return 1;
        }

        var listId = ArgValue(args, "--list") ?? config["ClickUp:ClientListId"] ?? ClickUpDiscoveryRunner.DefaultListId;

        using var http = ClickUpApi.CreateClient(token);
        var fields = await ClickUpApi.GetFieldsAsync(http, listId);
        if (fields is null) return 1;
        var tasks = await ClickUpApi.GetTasksAsync(http, listId, fields);

        var trials = tasks
            .Where(t => string.Equals(t.Field("Contract"), "Trial", StringComparison.OrdinalIgnoreCase)
                        && t.Field("Status") is { } st && ActiveTrialStatuses.Contains(st))
            .ToList();

        Console.WriteLine($"[mark-trials] mode={(commit ? "COMMIT" : "DRY-RUN")}  list={listId}");
        Console.WriteLine($"[mark-trials] {tasks.Count} tasks; {trials.Count} active trials in scope.\n");

        var options = new DbContextOptionsBuilder<RdDbContext>().UseSqlServer(conn).AddInterceptors(new AppendOnlyInterceptor()).Options;
        await using var db = new RdDbContext(options);
        var ct = CancellationToken.None;
        var now = DateTimeOffset.UtcNow;

        // ── Match indexes over existing clients (projections only) ───────────────
        var clients = await db.Clients
            .Select(c => new { c.Id, c.BusinessName, c.ContactName, c.Email, c.Phone, c.ContractType })
            .ToListAsync(ct);
        var haveTrialPeriod = (await db.TrialPeriods.AsNoTracking().Select(t => t.ClientId).Distinct().ToListAsync(ct)).ToHashSet();

        var byEmail = new Dictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase);
        var byPhone = new Dictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase);
        void Index(Dictionary<string, HashSet<Guid>> ix, string? key, Guid id)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            (ix.TryGetValue(key, out var set) ? set : ix[key] = new HashSet<Guid>()).Add(id);
        }
        foreach (var c in clients)
        {
            Index(byEmail, NormEmail(c.Email), c.Id);
            Index(byPhone, PhoneTail(c.Phone), c.Id);
            Index(byName, NameNormalizer.Normalize(c.BusinessName), c.Id);
            Index(byName, NameNormalizer.Normalize(c.ContactName), c.Id);
        }
        var contractById = clients.ToDictionary(c => c.Id, c => c.ContractType);
        var nameById = clients.ToDictionary(c => c.Id, c => c.BusinessName);

        Console.WriteLine($"[mark-trials] existing clients: {clients.Count}  (with email: {clients.Count(c => NormEmail(c.Email) is not null)}, with phone: {clients.Count(c => PhoneTail(c.Phone) is not null)})\n");

        // ── Match each ClickUp trial to at most one existing client ──────────────
        int matched = 0, alreadyTrial = 0, ambiguous = 0, unmatched = 0;
        var bySignal = new Dictionary<string, int> { ["email"] = 0, ["phone"] = 0, ["name"] = 0 };
        var processed = new HashSet<Guid>();
        var toMark = new List<Guid>();       // matched, not already Trial → flip ContractType
        var toAddTrialPeriod = new List<Guid>();
        var markedSample = new List<string>();

        foreach (var t in trials)
        {
            var (clientId, signal) = Resolve(t, byEmail, byPhone, byName);
            if (signal == "ambiguous") { ambiguous++; continue; }
            if (clientId is not { } id) { unmatched++; continue; }

            matched++;
            bySignal[signal]++;
            if (!processed.Add(id)) continue; // a client already handled from another ClickUp row

            if (contractById[id] == ContractType.Trial)
            {
                alreadyTrial++;
            }
            else
            {
                toMark.Add(id);
                if (markedSample.Count < 20) markedSample.Add($"{nameById[id]}  (via {signal})");
            }

            if (!noTrialPeriod && !haveTrialPeriod.Contains(id))
            {
                toAddTrialPeriod.Add(id);
                haveTrialPeriod.Add(id);
            }
        }

        Console.WriteLine($"[mark-trials] matched {matched} existing clients (email:{bySignal["email"]}, phone:{bySignal["phone"]}, name:{bySignal["name"]}).");
        Console.WriteLine($"[mark-trials]   → newly marked Trial:      {toMark.Count}");
        Console.WriteLine($"[mark-trials]   → already Trial:           {alreadyTrial}");
        Console.WriteLine($"[mark-trials]   → TrialPeriods to create:  {toAddTrialPeriod.Count}{(noTrialPeriod ? " (suppressed)" : " (null expiry — backfill in app)")}");
        Console.WriteLine($"[mark-trials] ambiguous (multiple clients matched, skipped): {ambiguous}");
        Console.WriteLine($"[mark-trials] ClickUp trials with NO match in the app (left alone): {unmatched}\n");
        if (markedSample.Count > 0)
        {
            Console.WriteLine("  Sample of clients to mark Trial:");
            foreach (var s in markedSample) Console.WriteLine($"    ~ {s}");
            if (toMark.Count > markedSample.Count) Console.WriteLine($"    … +{toMark.Count - markedSample.Count} more");
            Console.WriteLine();
        }

        if (commit)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            if (toMark.Count > 0)
                await db.Clients.Where(c => toMark.Contains(c.Id))
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.ContractType, ContractType.Trial), ct);

            foreach (var id in toAddTrialPeriod)
                db.TrialPeriods.Add(new TrialPeriod
                {
                    Id = Guid.NewGuid(), ClientId = id, StartsAt = now, ExpiresAt = null, Outcome = TrialOutcome.Active,
                });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            Console.WriteLine($"[mark-trials] COMMITTED — {toMark.Count} clients marked Trial, {toAddTrialPeriod.Count} TrialPeriods created. No new clients.");
        }
        else
        {
            Console.WriteLine("[mark-trials] DRY-RUN — nothing written. Re-run with --commit to persist.");
        }
        return 0;
    }

    /// <summary>Email → phone → name. Returns the single matched client, or ("ambiguous") when a signal hits several clients.</summary>
    private static (Guid? ClientId, string Signal) Resolve(
        ClickUpTask t,
        Dictionary<string, HashSet<Guid>> byEmail,
        Dictionary<string, HashSet<Guid>> byPhone,
        Dictionary<string, HashSet<Guid>> byName)
    {
        if (TaskEmail(t) is { } em && byEmail.TryGetValue(em, out var eset))
            return eset.Count == 1 ? (eset.First(), "email") : (null, "ambiguous");

        if (PhoneTail(t.Field("Ads Contact #")) is { } ph && byPhone.TryGetValue(ph, out var pset))
            return pset.Count == 1 ? (pset.First(), "phone") : (null, "ambiguous");

        var names = new[] { NameNormalizer.Normalize(t.Field("Business Name")), NameNormalizer.Normalize(t.Name) }
            .Where(n => n.Length > 0).Distinct();
        var hits = new HashSet<Guid>();
        foreach (var n in names)
            if (byName.TryGetValue(n, out var nset)) hits.UnionWith(nset);
        if (hits.Count == 1) return (hits.First(), "name");
        if (hits.Count > 1) return (null, "ambiguous");
        return (null, "none");
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    /// <summary>Best email a ClickUp task carries — the dedicated Email field, else the real address often stashed in "stripe email/link".</summary>
    private static string? TaskEmail(ClickUpTask t) => NormEmail(t.Field("Email")) ?? NormEmail(t.Field("stripe email/link"));

    private static string? NormEmail(string? raw)
    {
        var s = raw?.Trim().ToLowerInvariant();
        return !string.IsNullOrEmpty(s) && s.Contains('@') && !s.Contains(' ') ? s : null;
    }

    private static string? PhoneTail(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        return digits.Length >= 10 ? digits[^10..] : null;
    }

    private static string? ArgValue(string[] args, string flag)
    {
        var i = Array.FindIndex(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
