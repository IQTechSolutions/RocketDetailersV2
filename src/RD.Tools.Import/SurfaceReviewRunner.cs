using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using RD.Domain;
using RD.Domain.Entities;
using RD.Infrastructure;

namespace RD.Tools.Import;

/// <summary>
/// Surfaces the low-confidence reconciliation cases as investigation items:
///   • DISAGREE — a task's exact id points at a different client than its name match
///     (a duplicate app client or a mislinked/duplicated ClickUp contact). Kind=ImportConflict.
///   • name-only — a task matched a client by name with no id to confirm it. Kind=Other.
/// Deduped against existing OPEN investigations, so re-running is safe. Dry-run unless --commit.
///
///   dotnet run --project src/RD.Tools.Import -- surface-review [--commit] [--name-only-too]
/// </summary>
public static class SurfaceReviewRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        var commit = args.Any(a => a.Equals("--commit", StringComparison.OrdinalIgnoreCase));
        var includeNameOnly = args.Any(a => a.Equals("--name-only-too", StringComparison.OrdinalIgnoreCase));

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
        var now = DateTimeOffset.UtcNow;

        var index = await ClickUpMatchIndex.BuildAsync(db, ct);
        var nameById = (await db.Clients.Select(c => new { c.Id, c.BusinessName }).ToListAsync(ct))
            .ToDictionary(c => c.Id, c => c.BusinessName);

        // Existing OPEN investigations to dedupe against (by client + kind).
        var openByClientKind = (await db.InvestigationItems.AsNoTracking()
                .Where(i => i.Status == InvestigationStatus.Open && i.ClientId != null)
                .Select(i => new { i.ClientId, i.Kind }).ToListAsync(ct))
            .Select(i => (i.ClientId!.Value, i.Kind)).ToHashSet();

        var disagreements = new List<InvestigationItem>();
        var nameOnly = new List<InvestigationItem>();
        var seen = new HashSet<(Guid, InvestigationKind)>();

        foreach (var t in tasks)
        {
            var (exId, exSig) = index.ResolveExact(t);
            var (nmId, _) = index.ResolveName(t);

            if (exId is { } e && nmId is { } n && e != n)
            {
                // Anchor the item on the NAME-matched client (the one likely wrong / duplicated).
                if (seen.Add((n, InvestigationKind.ImportConflict)) && !openByClientKind.Contains((n, InvestigationKind.ImportConflict)))
                    disagreements.Add(new InvestigationItem
                    {
                        Id = Guid.NewGuid(), ClientId = n, Kind = InvestigationKind.ImportConflict,
                        Detail = $"ClickUp '{t.Name}' matches this client by name, but its {exSig} id belongs to '{nameById.GetValueOrDefault(e, "?")}'. Likely a duplicate client or a mislinked contact — merge or re-link.",
                        CreatedAt = now,
                    });
            }
            else if (exId is null && nmId is { } n2)
            {
                if (seen.Add((n2, InvestigationKind.Other)) && !openByClientKind.Contains((n2, InvestigationKind.Other)))
                    nameOnly.Add(new InvestigationItem
                    {
                        Id = Guid.NewGuid(), ClientId = n2, Kind = InvestigationKind.Other,
                        Detail = $"ClickUp '{t.Name}' matched this client by name only — no Stripe/GHL/Meta id to confirm it. Verify it's the same client.",
                        CreatedAt = now,
                    });
            }
        }

        Console.WriteLine($"[surface-review] mode={(commit ? "COMMIT" : "DRY-RUN")}  list={listId}");
        Console.WriteLine($"[surface-review] disagreements to raise (ImportConflict): {disagreements.Count}");
        Console.WriteLine($"[surface-review] name-only to raise (Other):            {nameOnly.Count}{(includeNameOnly ? "" : "  (skipped — pass --name-only-too to include)")}\n");
        foreach (var i in disagreements.Take(15)) Console.WriteLine($"    ! {i.Detail}");
        Console.WriteLine();

        var toWrite = includeNameOnly ? disagreements.Concat(nameOnly).ToList() : disagreements;

        if (commit)
        {
            db.InvestigationItems.AddRange(toWrite);
            await db.SaveChangesAsync(ct);
            Console.WriteLine($"[surface-review] COMMITTED — {toWrite.Count} investigations raised.");
        }
        else
        {
            Console.WriteLine($"[surface-review] DRY-RUN — nothing written ({toWrite.Count} would be raised). Re-run with --commit.");
        }
        return 0;
    }

    private static string? ArgValue(string[] args, string flag)
    {
        var i = Array.FindIndex(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
