using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using RD.Domain;
using RD.Domain.Entities;
using RD.Infrastructure;

namespace RD.Tools.Import;

/// <summary>
/// Matches every ClickUp Client Database task to an EXISTING app client (email →
/// phone → name) and attaches the client's Meta footprint from ClickUp:
///   • Meta/AdAccount — the ad-account id (own-account clients have their own;
///     master-account "users" share the master ad account, which the app treats
///     as a shared identity), read from `Ad account ID` → the `act=` param of
///     `Ad Account Link` → `FB AD ACC ID`.
///   • Meta/Campaign  — `selected_campaign_ids` from `Ad Account Link`, when present.
///
/// Never creates clients. Existing links are skipped; a campaign already owned by
/// a different client is flagged as an ImportConflict, not overwritten. Reads are
/// projections and writes are plain inserts, so unrelated schema drift is a
/// non-issue. Dry-run unless --commit.
///
///   dotnet run --project src/RD.Tools.Import -- link-meta-clickup [--list &lt;id&gt;] [--commit]
/// </summary>
public static class LinkMetaClickUpRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        var commit = args.Any(a => a.Equals("--commit", StringComparison.OrdinalIgnoreCase));
        // By default we skip the shared master ad account (it's not a per-client identity);
        // pass --include-master-act to attach it to master-account users as well.
        var includeMasterAct = args.Any(a => a.Equals("--include-master-act", StringComparison.OrdinalIgnoreCase));

        var config = new ConfigurationBuilder()
            .AddUserSecrets(Assembly.GetExecutingAssembly(), optional: true)
            .AddEnvironmentVariables()
            .Build();

        var token = config["ClickUp:ApiToken"];
        if (string.IsNullOrWhiteSpace(token)) { Console.WriteLine("ERROR: set ClickUp:ApiToken in user-secrets."); return 1; }
        var conn = Environment.GetEnvironmentVariable("RD_CONN") ?? config.GetConnectionString("RocketDetailers");
        if (string.IsNullOrWhiteSpace(conn)) { Console.WriteLine("ERROR: set ConnectionStrings:RocketDetailers in user-secrets or RD_CONN."); return 1; }

        var listId = ArgValue(args, "--list") ?? config["ClickUp:ClientListId"] ?? ClickUpDiscoveryRunner.DefaultListId;

        using var http = ClickUpApi.CreateClient(token);
        var fields = await ClickUpApi.GetFieldsAsync(http, listId);
        if (fields is null) return 1;
        var tasks = await ClickUpApi.GetTasksAsync(http, listId, fields);

        Console.WriteLine($"[link-meta-clickup] mode={(commit ? "COMMIT" : "DRY-RUN")}  list={listId}");
        Console.WriteLine($"[link-meta-clickup] {tasks.Count} ClickUp tasks.");

        // The master ad account is the one shared across the most tasks — clients on it are
        // "master-account users"; a different act is a client's own (specific) meta account.
        var acctCounts = tasks.Select(AdAccountId).Where(a => a is not null)
            .GroupBy(a => a!).Select(g => new { Act = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count).ToList();
        var configMaster = Digits(config["Meta:AdAccountId"]);
        var masterAct = configMaster ?? acctCounts.FirstOrDefault()?.Act;
        Console.WriteLine($"[link-meta-clickup] master ad account: act_{masterAct}"
            + (configMaster is not null ? " (from config)" : $" (detected — {acctCounts.FirstOrDefault()?.Count} tasks share it)") + "\n");

        var options = new DbContextOptionsBuilder<RdDbContext>().UseSqlServer(conn).AddInterceptors(new AppendOnlyInterceptor()).Options;
        await using var db = new RdDbContext(options);
        var ct = CancellationToken.None;
        var now = DateTimeOffset.UtcNow;

        // ── Match indexes over existing clients (projections only) ───────────────
        var clients = await db.Clients
            .Select(c => new { c.Id, c.BusinessName, c.ContactName, c.Email, c.Phone })
            .ToListAsync(ct);
        var nameById = clients.ToDictionary(c => c.Id, c => c.BusinessName);

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

        // ── Existing Meta links (dedup + campaign conflict) ──────────────────────
        var clientHasLink = new HashSet<(Guid, LinkKind, string)>();
        var campaignOwner = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in await db.IdentityLinks.AsNoTracking()
                     .Where(l => l.System == ExternalSystem.Meta && l.InvalidatedAt == null
                                 && (l.Kind == LinkKind.AdAccount || l.Kind == LinkKind.Campaign))
                     .Select(l => new { l.ClientId, l.Kind, l.ExternalId }).ToListAsync(ct))
        {
            clientHasLink.Add((l.ClientId, l.Kind, l.ExternalId));
            if (l.Kind == LinkKind.Campaign) campaignOwner[l.ExternalId] = l.ClientId;
        }

        int matched = 0, masterUsers = 0, ownAccounts = 0, noMeta = 0, unmatched = 0, ambiguous = 0;
        int adAccountAdds = 0, campaignAdds = 0, alreadyLinked = 0, conflicts = 0, masterActSkipped = 0;
        var bySignal = new Dictionary<string, int> { ["email"] = 0, ["phone"] = 0, ["name"] = 0 };
        var processed = new HashSet<Guid>();
        var ownSample = new List<string>();
        var toAdd = new List<(Guid ClientId, LinkKind Kind, string ExternalId)>();
        var toFlagConflict = new List<(Guid ClientId, string CampaignId)>();

        foreach (var t in tasks)
        {
            var (clientId, signal) = Resolve(t, byEmail, byPhone, byName);
            if (signal == "ambiguous") { ambiguous++; continue; }
            if (clientId is not { } id) { unmatched++; continue; }

            matched++;
            bySignal[signal]++;
            if (!processed.Add(id)) continue; // one ClickUp row per client (first match wins)

            var adAccountId = AdAccountId(t);
            var campaignId = ClickUpApi.MetaCampaignId(t.Field("Ad Account Link"));

            // Classify by the actual ad account, not the (often wrong) Master flag.
            var onMaster = adAccountId is not null && adAccountId == masterAct;
            var flaggedMaster = string.Equals(t.Field("Master Ad Account?"), "Yes", StringComparison.OrdinalIgnoreCase);
            if (onMaster || (adAccountId is null && flaggedMaster)) masterUsers++;
            else if (adAccountId is not null) { ownAccounts++; if (ownSample.Count < 20) ownSample.Add($"{nameById[id]} → act_{adAccountId}"); }
            else noMeta++;

            // Attach own/specific ad accounts; skip the shared master act unless explicitly asked.
            if (adAccountId is not null && (includeMasterAct || adAccountId != masterAct))
            {
                if (clientHasLink.Add((id, LinkKind.AdAccount, adAccountId))) { toAdd.Add((id, LinkKind.AdAccount, adAccountId)); adAccountAdds++; }
                else alreadyLinked++;
            }
            else if (adAccountId is not null && adAccountId == masterAct)
            {
                masterActSkipped++;
            }
            if (campaignId is not null)
            {
                if (campaignOwner.TryGetValue(campaignId, out var owner) && owner != id)
                {
                    toFlagConflict.Add((id, campaignId));
                    conflicts++;
                }
                else if (clientHasLink.Add((id, LinkKind.Campaign, campaignId)))
                {
                    toAdd.Add((id, LinkKind.Campaign, campaignId));
                    campaignOwner[campaignId] = id;
                    campaignAdds++;
                }
                else alreadyLinked++;
            }
        }

        Console.WriteLine($"[link-meta-clickup] matched {matched} existing clients (email:{bySignal["email"]}, phone:{bySignal["phone"]}, name:{bySignal["name"]}).");
        Console.WriteLine($"[link-meta-clickup]   classification: master-account users: {masterUsers}, own ad accounts: {ownAccounts}, no Meta info: {noMeta}");
        Console.WriteLine($"[link-meta-clickup]   Meta ad-account links to add: {adAccountAdds} (own/specific)");
        Console.WriteLine($"[link-meta-clickup]   shared master-act links {(includeMasterAct ? "included" : "skipped")}: {masterActSkipped}");
        Console.WriteLine($"[link-meta-clickup]   Meta campaign links to add:   {campaignAdds}");
        Console.WriteLine($"[link-meta-clickup]   already linked (skipped):     {alreadyLinked}");
        Console.WriteLine($"[link-meta-clickup]   campaign conflicts flagged:   {conflicts}");
        Console.WriteLine($"[link-meta-clickup] unmatched ClickUp tasks: {unmatched}   ambiguous: {ambiguous}\n");
        if (ownSample.Count > 0)
        {
            Console.WriteLine("  Sample own-account clients → ad account:");
            foreach (var s in ownSample) Console.WriteLine($"    · {s}");
            if (ownAccounts > ownSample.Count) Console.WriteLine($"    … +{ownAccounts - ownSample.Count} more");
            Console.WriteLine();
        }

        if (commit)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            foreach (var (clientId, kind, externalId) in toAdd)
                db.IdentityLinks.Add(new IdentityLink
                {
                    Id = Guid.NewGuid(), ClientId = clientId, System = ExternalSystem.Meta, Kind = kind, ExternalId = externalId, CreatedAt = now,
                });
            foreach (var (clientId, campaignId) in toFlagConflict)
                db.InvestigationItems.Add(new InvestigationItem
                {
                    Id = Guid.NewGuid(), ClientId = clientId, Kind = InvestigationKind.ImportConflict,
                    Detail = $"ClickUp maps Meta campaign {campaignId} to this client, but it's already linked to another client. Resolve which owns it.",
                    System = ExternalSystem.Meta, ExternalId = campaignId, CreatedAt = now,
                });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            Console.WriteLine($"[link-meta-clickup] COMMITTED — {adAccountAdds} ad-account links, {campaignAdds} campaign links, {conflicts} conflicts flagged. No new clients.");
        }
        else
        {
            Console.WriteLine("[link-meta-clickup] DRY-RUN — nothing written. Re-run with --commit to persist.");
        }
        return 0;
    }

    /// <summary>Ad-account id from ClickUp: the field, else the act= in the Ads Manager URL, else the FB ad-account field.</summary>
    private static string? AdAccountId(ClickUpTask t)
        => Digits(t.Field("Ad account ID"))
           ?? ClickUpApi.MetaAdAccountFromUrl(t.Field("Ad Account Link"))
           ?? Digits(t.Field("FB AD ACC ID"));

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

    private static string? Digits(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var d = new string(raw.Where(char.IsDigit).ToArray());
        return d.Length >= 5 ? d : null;
    }

    private static string? ArgValue(string[] args, string flag)
    {
        var i = Array.FindIndex(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
