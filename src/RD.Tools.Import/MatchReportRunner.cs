using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using RD.Domain;
using RD.Infrastructure;

namespace RD.Tools.Import;

/// <summary>
/// READ-ONLY. The GHL-anchored exact-id matcher, run as an analysis. For every
/// ClickUp task it resolves the app client TWO ways — by exact id (GHL contact →
/// Stripe customer → Meta campaign; all unique-per-client, so unambiguous) and by
/// the old fuzzy signal (email → phone → name) — and reports where they agree,
/// disagree (a name match the ids say is WRONG), or where name guessed alone
/// (unconfirmed). Also sizes the phone/email backfill the exact matches unlock.
/// Writes NOTHING.
///
///   dotnet run --project src/RD.Tools.Import -- match-report [--list &lt;id&gt;]
/// </summary>
public static class MatchReportRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
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

        var options = new DbContextOptionsBuilder<RdDbContext>().UseSqlServer(conn).Options;
        await using var db = new RdDbContext(options);
        var ct = CancellationToken.None;

        // ── App side ─────────────────────────────────────────────────────────────
        var clients = await db.Clients
            .Select(c => new { c.Id, c.BusinessName, c.ContactName, c.Email, c.Phone })
            .ToListAsync(ct);
        var nameById = clients.ToDictionary(c => c.Id, c => c.BusinessName);
        var hasEmail = clients.Where(c => NormEmail(c.Email) is not null).Select(c => c.Id).ToHashSet();
        var hasPhone = clients.Where(c => PhoneTail(c.Phone) is not null).Select(c => c.Id).ToHashSet();

        var ghlToClient = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var stripeToClient = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var campaignToClient = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in await db.IdentityLinks.AsNoTracking()
                     .Where(l => l.InvalidatedAt == null)
                     .Select(l => new { l.System, l.Kind, l.ExternalId, l.ClientId }).ToListAsync(ct))
        {
            if (l.System == ExternalSystem.Ghl && l.Kind == LinkKind.Contact) ghlToClient[l.ExternalId] = l.ClientId;
            else if (l.System == ExternalSystem.Stripe && l.Kind == LinkKind.Customer) stripeToClient[l.ExternalId] = l.ClientId;
            else if (l.System == ExternalSystem.Meta && l.Kind == LinkKind.Campaign) campaignToClient[l.ExternalId] = l.ClientId;
        }

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

        // ── Resolve every task both ways ─────────────────────────────────────────
        int agree = 0, exactOnly = 0, nameOnly = 0, disagree = 0, neither = 0;
        int ghlHits = 0, stripeHits = 0, campaignHits = 0;
        int backfillPhone = 0, backfillEmail = 0;
        var exactClients = new HashSet<Guid>();
        var nameOnlyClients = new HashSet<Guid>();
        var disagreeSample = new List<string>();

        foreach (var t in tasks)
        {
            var (exactId, exactSig) = ResolveExact(t, ghlToClient, stripeToClient, campaignToClient);
            var nameId = ResolveName(t, byEmail, byPhone, byName);

            if (exactId is { } ex)
            {
                exactClients.Add(ex);
                switch (exactSig) { case "ghl": ghlHits++; break; case "stripe": stripeHits++; break; case "campaign": campaignHits++; break; }

                // Backfill sizing: we now KNOW this client — does ClickUp fill an app gap?
                if (!hasPhone.Contains(ex) && PhoneTail(t.Field("Ads Contact #")) is not null) backfillPhone++;
                if (!hasEmail.Contains(ex) && TaskEmail(t) is not null) backfillEmail++;
            }

            if (exactId is { } e && nameId is { } n)
            {
                if (e == n) agree++;
                else { disagree++; if (disagreeSample.Count < 15) disagreeSample.Add($"{t.Name}: id→{nameById[e]}  vs  name→{nameById[n]}"); }
            }
            else if (exactId is not null) exactOnly++;
            else if (nameId is { } n2) { nameOnly++; nameOnlyClients.Add(n2); }
            else neither++;
        }

        Console.WriteLine($"[match-report] list={listId}  (read-only)\n");
        Console.WriteLine($"App clients: {clients.Count}   ClickUp tasks: {tasks.Count}\n");

        Console.WriteLine("=== Exact-id resolution (unique-per-client, so unambiguous) ===");
        Console.WriteLine($"  by GHL contact:   {ghlHits} task hits");
        Console.WriteLine($"  by Stripe cus:    {stripeHits} task hits");
        Console.WriteLine($"  by Meta campaign: {campaignHits} task hits");
        Console.WriteLine($"  distinct clients reached by an exact id: {exactClients.Count} / {clients.Count}\n");

        Console.WriteLine("=== Exact-id vs name agreement (per task) ===");
        Console.WriteLine($"  = agree (name CONFIRMED by an id):      {agree}");
        Console.WriteLine($"  + exact-id only (name missed it):       {exactOnly}");
        Console.WriteLine($"  ? name only (no id to confirm):         {nameOnly}   -> {nameOnlyClients.Count} distinct clients, lower confidence");
        Console.WriteLine($"  X DISAGREE (name match was WRONG):      {disagree}   <- id says a different client");
        Console.WriteLine($"  – neither:                              {neither}\n");
        if (disagreeSample.Count > 0)
        {
            Console.WriteLine("  Sample disagreements (name matched the wrong client):");
            foreach (var s in disagreeSample) Console.WriteLine($"    ! {s}");
            if (disagree > disagreeSample.Count) Console.WriteLine($"    … +{disagree - disagreeSample.Count} more");
            Console.WriteLine();
        }

        Console.WriteLine("=== Backfill unlocked (for exact-id-matched clients) ===");
        Console.WriteLine($"  clients missing a phone in the app, ClickUp has one: {backfillPhone}");
        Console.WriteLine($"  clients missing an email in the app, ClickUp has one: {backfillEmail}\n");

        Console.WriteLine("[match-report] done — read-only, nothing written.");
        return 0;
    }

    private static (Guid? ClientId, string Signal) ResolveExact(
        ClickUpTask t,
        Dictionary<string, Guid> ghl, Dictionary<string, Guid> stripe, Dictionary<string, Guid> campaign)
    {
        var ghlId = NullIf(t.Field("GHL CONTACT ID")) ?? ClickUpApi.GhlContactId(t.Field("GHL Contact"));
        if (ghlId is not null && ghl.TryGetValue(ghlId, out var g)) return (g, "ghl");

        var cus = t.Field("Stripe Customer ID");
        if (cus is not null && cus.Trim().StartsWith("cus_", StringComparison.OrdinalIgnoreCase) && stripe.TryGetValue(cus.Trim(), out var s)) return (s, "stripe");

        var camp = ClickUpApi.MetaCampaignId(t.Field("Ad Account Link"));
        if (camp is not null && campaign.TryGetValue(camp, out var c)) return (c, "campaign");

        return (null, "none");
    }

    private static Guid? ResolveName(
        ClickUpTask t,
        Dictionary<string, HashSet<Guid>> byEmail, Dictionary<string, HashSet<Guid>> byPhone, Dictionary<string, HashSet<Guid>> byName)
    {
        if (TaskEmail(t) is { } em && byEmail.TryGetValue(em, out var eset)) return eset.Count == 1 ? eset.First() : null;
        if (PhoneTail(t.Field("Ads Contact #")) is { } ph && byPhone.TryGetValue(ph, out var pset)) return pset.Count == 1 ? pset.First() : null;
        var names = new[] { NameNormalizer.Normalize(t.Field("Business Name")), NameNormalizer.Normalize(t.Name) }.Where(n => n.Length > 0).Distinct();
        var hits = new HashSet<Guid>();
        foreach (var n in names) if (byName.TryGetValue(n, out var nset)) hits.UnionWith(nset);
        return hits.Count == 1 ? hits.First() : null;
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
        var d = new string(raw.Where(char.IsDigit).ToArray());
        return d.Length >= 10 ? d[^10..] : null;
    }

    private static string? NullIf(string? raw) => string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

    private static string? ArgValue(string[] args, string flag)
    {
        var i = Array.FindIndex(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
