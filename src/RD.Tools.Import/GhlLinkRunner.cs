using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using RD.Domain;
using RD.Domain.Entities;
using RD.Infrastructure;

namespace RD.Tools.Import;

/// <summary>
/// GHL contact linking from the master sheet — the mapping's third leg. No GHL
/// API is needed: the link comes entirely from the sheet's GHL Contact ID column
/// (the token is only required later, to actually SEND dunning messages).
///
///   dotnet run --project src/RD.Tools.Import -- link-ghl [--commit] [--crosswalk &lt;xlsx&gt;]
///
/// Bridges Stripe customer → sheet row → GHL Contact ID and links it to the
/// client. The schema keeps ONE active GHL contact per client, so a client mapped
/// to several contacts links the first and flags the rest to confirm the primary;
/// a contact mapped to two clients is flagged. Idempotent; dry-run by default;
/// --commit to persist. Connection: RD_CONN or ConnectionStrings:RocketDetailers.
/// </summary>
public static class GhlLinkRunner
{
    private const string DefaultCrosswalkName = "All Clients - completed Final.xlsx";

    public static async Task<int> RunAsync(string[] args)
    {
        var commit = args.Any(a => a.Equals("--commit", StringComparison.OrdinalIgnoreCase));
        var crosswalkPath = ResolveCrosswalkPath(args);

        var conn = Environment.GetEnvironmentVariable("RD_CONN");
        if (string.IsNullOrWhiteSpace(conn))
        {
            Console.WriteLine("ERROR: set the RD_CONN environment variable to the target connection string.");
            return 1;
        }
        if (crosswalkPath is null)
        {
            Console.WriteLine($"ERROR: crosswalk sheet not found. Pass --crosswalk <xlsx> or place '{DefaultCrosswalkName}' in Downloads.");
            return 1;
        }

        Console.WriteLine($"[link-ghl] mode={(commit ? "COMMIT" : "DRY-RUN")}");
        var crosswalk = LoadGhlCrosswalk(crosswalkPath);
        Console.WriteLine($"[link-ghl] crosswalk: {crosswalk.Count} Stripe-customer→GHL-contact mappings from {Path.GetFileName(crosswalkPath)}.");
        if (crosswalk.Count == 0)
        {
            Console.WriteLine("[link-ghl] nothing to link (need the sheet's 'GHL Contact ID' + 'Stripe Customer ID' columns).");
            return 1;
        }

        var options = new DbContextOptionsBuilder<RdDbContext>()
            .UseSqlServer(conn)
            .AddInterceptors(new AppendOnlyInterceptor())
            .Options;
        await using var db = new RdDbContext(options);
        var ct = CancellationToken.None;

        var stripeCustomers = await db.IdentityLinks.AsNoTracking()
            .Where(l => l.System == ExternalSystem.Stripe && l.Kind == LinkKind.Customer && l.InvalidatedAt == null)
            .Select(l => new { l.ClientId, l.ExternalId })
            .ToListAsync(ct);
        var customersByClient = stripeCustomers.GroupBy(x => x.ClientId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.ExternalId).ToList());

        // Existing GHL contact links → idempotency (one contact per client) + owner map.
        var contactOwner = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var clientsWithContact = new HashSet<Guid>();
        foreach (var l in await db.IdentityLinks.AsNoTracking()
                     .Where(l => l.System == ExternalSystem.Ghl && l.Kind == LinkKind.Contact && l.InvalidatedAt == null)
                     .Select(l => new { l.ClientId, l.ExternalId }).ToListAsync(ct))
        {
            contactOwner[l.ExternalId] = l.ClientId;
            clientsWithContact.Add(l.ClientId);
        }

        var now = DateTimeOffset.UtcNow;
        int linked = 0, clientsNoMapping = 0, alreadyLinked = 0, conflicts = 0, multiContact = 0, flags = 0;

        void Flag(Guid clientId, InvestigationKind kind, string detail)
        {
            if (detail.Length > 1000) detail = detail[..997] + "...";
            db.InvestigationItems.Add(new InvestigationItem
            {
                Id = Guid.NewGuid(), ClientId = clientId, Kind = kind, Detail = detail, CreatedAt = now,
            });
            flags++;
        }

        foreach (var (clientId, custIds) in customersByClient)
        {
            if (clientsWithContact.Contains(clientId)) { alreadyLinked++; continue; }

            var mapped = custIds
                .SelectMany(cid => crosswalk.TryGetValue(cid, out var cs) ? cs : Enumerable.Empty<string>())
                .Distinct().ToList();
            if (mapped.Count == 0) { clientsNoMapping++; continue; }

            // One GHL contact per client (schema): pick the first not owned by another client.
            string? chosen = null;
            foreach (var contactId in mapped)
            {
                if (contactOwner.TryGetValue(contactId, out var owner) && owner != clientId)
                {
                    Flag(clientId, InvestigationKind.ImportConflict,
                        $"GHL contact {contactId} maps to this client but is already linked to another client ({owner}). Resolve which owns it.");
                    conflicts++;
                    continue;
                }
                chosen = contactId;
                break;
            }
            if (chosen is null) continue;

            db.IdentityLinks.Add(new IdentityLink
            {
                Id = Guid.NewGuid(), ClientId = clientId, System = ExternalSystem.Ghl,
                Kind = LinkKind.Contact, ExternalId = chosen, CreatedAt = now,
            });
            contactOwner[chosen] = clientId;
            clientsWithContact.Add(clientId);
            linked++;

            if (mapped.Count > 1)
            {
                Flag(clientId, InvestigationKind.Other,
                    $"Client maps to {mapped.Count} GHL contacts ({string.Join(", ", mapped)}); linked {chosen} as primary — confirm.");
                multiContact++;
            }
        }

        Console.WriteLine($"[link-ghl] linked {linked} GHL contacts ({alreadyLinked} clients already had one).");
        Console.WriteLine($"[link-ghl] clients with no sheet contact mapping: {clientsNoMapping}.");
        Console.WriteLine($"[link-ghl] flags: {flags}  —  multi-contact-confirm-primary:{multiContact}  contact-claimed-by-two-clients:{conflicts}");

        if (commit)
        {
            await db.SaveChangesAsync(ct);
            Console.WriteLine($"[link-ghl] COMMITTED {linked} contact links + {flags} investigation items (all Shadow — nothing enforces).");
        }
        else
        {
            Console.WriteLine("[link-ghl] DRY-RUN — nothing written. Re-run with --commit to persist.");
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

    /// <summary>Stripe customer id → GHL contact id(s), from the master sheet.</summary>
    private static Dictionary<string, List<string>> LoadGhlCrosswalk(string path)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var wb = new XLWorkbook(path);
            var ws = wb.Worksheet("All Clients");
            var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var cell in ws.Row(4).CellsUsed()) headers[cell.GetString().Trim()] = cell.Address.ColumnNumber;
            int? Col(string sub) => headers.Where(kv => kv.Key.Contains(sub, StringComparison.OrdinalIgnoreCase))
                .Select(kv => (int?)kv.Value).FirstOrDefault();

            var contactCol = Col("GHL Contact ID");
            var idCols = new[] { Col("Stripe Customer ID (cus"), Col("Stripe Customer ID 2") }.Where(c => c is not null).ToList();
            if (contactCol is null || idCols.Count == 0) return map;

            var last = ws.LastRowUsed()!.RowNumber();
            for (var r = 5; r <= last; r++)
            {
                var contact = ws.Row(r).Cell(contactCol.Value).GetString().Trim();
                if (contact.Length is 0 or > 100) continue;
                foreach (var c in idCols)
                {
                    var cid = ws.Row(r).Cell(c!.Value).GetString().Trim();
                    if (!cid.StartsWith("cus_", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!map.TryGetValue(cid, out var list)) map[cid] = list = [];
                    if (!list.Contains(contact)) list.Add(contact);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[link-ghl] crosswalk load failed ({ex.GetType().Name}: {ex.Message}).");
        }
        return map;
    }
}
