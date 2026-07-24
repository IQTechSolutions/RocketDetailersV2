using System.Reflection;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RD.Domain;
using RD.Domain.Entities;
using RD.Infrastructure;
using RD.Infrastructure.Gateways;
using RD.Infrastructure.Sync;

namespace RD.Tools.Import;

/// <summary>
/// Read-only Meta linking pass: attach each discovered client's Meta campaign(s)
/// using the master sheet's Stripe-customer-id → Meta-campaign-id crosswalk,
/// verified against the campaigns that ACTUALLY live in the master ad account.
///
///   dotnet run --project src/RD.Tools.Import -- link-meta [--commit] [--crosswalk &lt;xlsx&gt;]
///
/// Deterministic, never guessed: it links a Meta campaign to a client only when
/// the sheet maps it (via one of the client's Stripe customers) AND the campaign
/// exists live in the master account. Sheet-mapped campaigns not found live
/// (own-account clients or stale ids) and campaigns claimed by two clients are
/// flagged for triage; live campaigns mapped to no client are reported.
///
/// Idempotent (existing links are skipped). Dry-run by default; --commit to
/// persist. NOTHING writes to Meta. Credentials: Meta:AccessToken + Meta:AdAccountId
/// from user-secrets/env. Connection: RD_CONN or ConnectionStrings:RocketDetailers.
/// </summary>
public static class MetaLinkRunner
{
    private const string DefaultCrosswalkName = "All Clients - completed Final.xlsx";

    public static async Task<int> RunAsync(string[] args)
    {
        var commit = args.Any(a => a.Equals("--commit", StringComparison.OrdinalIgnoreCase));
        var crosswalkPath = ResolveCrosswalkPath(args);

        var config = new ConfigurationBuilder()
            .AddUserSecrets(Assembly.GetExecutingAssembly(), optional: true)
            .AddEnvironmentVariables()
            .Build();

        var conn = Environment.GetEnvironmentVariable("RD_CONN") ?? config.GetConnectionString("RocketDetailers");
        if (string.IsNullOrWhiteSpace(conn))
        {
            Console.WriteLine("ERROR: set RD_CONN (or ConnectionStrings:RocketDetailers) to the target database.");
            return 1;
        }
        var adAccountId = config["Meta:AdAccountId"];
        if (string.IsNullOrWhiteSpace(config["Meta:AccessToken"]) || string.IsNullOrWhiteSpace(adAccountId))
        {
            Console.WriteLine("ERROR: set Meta:AccessToken and Meta:AdAccountId (stored outside the repo):");
            Console.WriteLine("  dotnet user-secrets set \"Meta:AccessToken\" \"<token>\" --project src/RD.Tools.Import");
            Console.WriteLine("  dotnet user-secrets set \"Meta:AdAccountId\" \"act_...\" --project src/RD.Tools.Import");
            return 1;
        }

        var services = new ServiceCollection();
        services.AddLogging(l => l.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IConfiguration>(config);
        services.AddDbContextFactory<RdDbContext>(o => o.UseSqlServer(conn).AddInterceptors(new AppendOnlyInterceptor()));
        services.AddRdSync(config);

        await using var provider = services.BuildServiceProvider();
        var meta = provider.GetRequiredService<IMetaAdsGateway>();
        var factory = provider.GetRequiredService<IDbContextFactory<RdDbContext>>();
        var ct = CancellationToken.None;

        Console.WriteLine($"[link-meta] mode={(commit ? "COMMIT" : "DRY-RUN")}  account={adAccountId}");

        // Crosswalk: Stripe customer id → the campaign id(s) that row maps to.
        var crosswalk = LoadMetaCrosswalk(crosswalkPath);
        Console.WriteLine(crosswalkPath is not null
            ? $"[link-meta] crosswalk: {crosswalk.Count} Stripe-customer→campaign mappings from {Path.GetFileName(crosswalkPath)}."
            : "[link-meta] crosswalk: none — nothing to link (need the sheet's Meta Campaign ID column).");

        Console.WriteLine("[link-meta] pulling live campaigns from the master ad account (read-only)…");
        IReadOnlyList<MetaCampaignDto> live;
        try
        {
            live = await meta.ListCampaignsAsync(adAccountId!, ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[link-meta] Meta read FAILED: {ex.Message}");
            Console.WriteLine("[link-meta] Check the token scope (ads_read) and that it can see this ad account.");
            return 1;
        }
        var liveIds = live.Select(c => c.Id).ToHashSet();
        Console.WriteLine($"[link-meta] master account has {live.Count} campaigns.");

        await using var db = await factory.CreateDbContextAsync(ct);

        // Each client's Stripe customer ids (the bridge into the crosswalk).
        var stripeCustomers = await db.IdentityLinks.AsNoTracking()
            .Where(l => l.System == ExternalSystem.Stripe && l.Kind == LinkKind.Customer && l.InvalidatedAt == null)
            .Select(l => new { l.ClientId, l.ExternalId })
            .ToListAsync(ct);
        var customersByClient = stripeCustomers.GroupBy(x => x.ClientId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.ExternalId).ToList());

        // Existing campaign links → idempotency + who already owns a campaign.
        var campaignOwner = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in await db.IdentityLinks.AsNoTracking()
                     .Where(l => l.System == ExternalSystem.Meta && l.Kind == LinkKind.Campaign)
                     .Select(l => new { l.ClientId, l.ExternalId }).ToListAsync(ct))
            campaignOwner[l.ExternalId] = l.ClientId;

        // Open flags already on file — so a re-run doesn't duplicate identical items.
        var existingFlags = (await db.InvestigationItems.AsNoTracking()
                .Where(i => i.Status == InvestigationStatus.Open)
                .Select(i => new { i.ClientId, i.Kind, i.Detail })
                .ToListAsync(ct))
            .Select(i => (i.ClientId, i.Kind, i.Detail)).ToHashSet();

        var now = DateTimeOffset.UtcNow;
        int linked = 0, clientsLinked = 0, clientsNoMapping = 0, staleNotFound = 0, conflicts = 0, alreadyLinked = 0, flags = 0;

        void Flag(Guid? clientId, InvestigationKind kind, string detail)
        {
            if (detail.Length > 1000) detail = detail[..997] + "...";
            if (!existingFlags.Add((clientId, kind, detail))) return; // identical open flag already present
            db.InvestigationItems.Add(new InvestigationItem
            {
                Id = Guid.NewGuid(), ClientId = clientId, Kind = kind, Detail = detail, CreatedAt = now,
            });
            flags++;
        }

        foreach (var (clientId, custIds) in customersByClient)
        {
            var mapped = custIds
                .SelectMany(cid => crosswalk.TryGetValue(cid, out var camps) ? camps : Enumerable.Empty<string>())
                .Distinct().ToList();
            if (mapped.Count == 0) { clientsNoMapping++; continue; }

            var gotOne = false;
            foreach (var campId in mapped)
            {
                if (!liveIds.Contains(campId))
                {
                    Flag(clientId, InvestigationKind.StaleSync,
                        $"Sheet maps Meta campaign {campId} to this client, but it is not in the master ad account — own-account client or a stale campaign id. Reconcile.");
                    staleNotFound++;
                    continue;
                }
                if (campaignOwner.TryGetValue(campId, out var owner))
                {
                    if (owner == clientId) { alreadyLinked++; gotOne = true; }
                    else
                    {
                        Flag(clientId, InvestigationKind.ImportConflict,
                            $"Meta campaign {campId} is mapped to this client but already linked to another client ({owner}). Resolve which owns it.");
                        conflicts++;
                    }
                    continue;
                }

                db.IdentityLinks.Add(new IdentityLink
                {
                    Id = Guid.NewGuid(), ClientId = clientId, System = ExternalSystem.Meta,
                    Kind = LinkKind.Campaign, ExternalId = campId, CreatedAt = now,
                });
                campaignOwner[campId] = clientId;
                linked++;
                gotOne = true;
            }
            if (gotOne) clientsLinked++;
        }

        var orphanLive = liveIds.Count(id => !campaignOwner.ContainsKey(id));

        Console.WriteLine($"[link-meta] linked {linked} campaigns to {clientsLinked} clients ({alreadyLinked} already linked).");
        Console.WriteLine($"[link-meta] clients with no sheet campaign mapping: {clientsNoMapping} (surface in the cockpit's mapping wizard).");
        Console.WriteLine($"[link-meta] flags: {flags}  —  sheet-campaign-not-in-account:{staleNotFound}  campaign-claimed-by-two-clients:{conflicts}");
        Console.WriteLine($"[link-meta] live campaigns mapped to no client (orphans): {orphanLive}.");

        if (commit)
        {
            await db.SaveChangesAsync(ct);
            Console.WriteLine($"[link-meta] COMMITTED {linked} campaign links + {flags} investigation items (all Shadow — nothing enforces).");
        }
        else
        {
            Console.WriteLine("[link-meta] DRY-RUN — nothing written. Re-run with --commit to persist.");
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

    /// <summary>Stripe customer id → Meta campaign id(s), from the master sheet (cols "Stripe Customer ID"/"...ID 2" and "Meta Campaign ID").</summary>
    private static Dictionary<string, List<string>> LoadMetaCrosswalk(string? path)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (path is null) return map;
        try
        {
            using var wb = new XLWorkbook(path);
            var ws = wb.Worksheet("All Clients");
            var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var cell in ws.Row(4).CellsUsed()) headers[cell.GetString().Trim()] = cell.Address.ColumnNumber;
            int? Col(string sub) => headers.Where(kv => kv.Key.Contains(sub, StringComparison.OrdinalIgnoreCase))
                .Select(kv => (int?)kv.Value).FirstOrDefault();

            var campCol = Col("Meta Campaign ID");
            var idCols = new[] { Col("Stripe Customer ID (cus"), Col("Stripe Customer ID 2") }.Where(c => c is not null).ToList();
            if (campCol is null || idCols.Count == 0) return map;

            var last = ws.LastRowUsed()!.RowNumber();
            for (var r = 5; r <= last; r++)
            {
                var camp = ws.Row(r).Cell(campCol.Value).GetString().Trim();
                if (camp.Length == 0) continue;
                foreach (var c in idCols)
                {
                    var cid = ws.Row(r).Cell(c!.Value).GetString().Trim();
                    if (!cid.StartsWith("cus_", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!map.TryGetValue(cid, out var list)) map[cid] = list = [];
                    if (!list.Contains(camp)) list.Add(camp);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[link-meta] crosswalk load skipped ({ex.GetType().Name}: {ex.Message}).");
        }
        return map;
    }
}
