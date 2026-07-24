using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using RD.Domain;
using RD.Domain.Entities;
using RD.Infrastructure;
using RD.Tools.Import;

// "sync" verb: one-shot vendor sync + policy evaluation (see SyncRunner).
if (args.Length > 0 && args[0].Equals("sync", StringComparison.OrdinalIgnoreCase))
    return await SyncRunner.RunAsync(args);

// "seed-user" verb: create/reset an admin directly (see SeedUserRunner).
if (args.Length > 0 && args[0].Equals("seed-user", StringComparison.OrdinalIgnoreCase))
    return await SeedUserRunner.RunAsync(args);

// Seed importer: one-time load of the reconciliation spreadsheet into SQL.
// Disposable by design (design doc: "one-time spreadsheet import code").
// Usage: dotnet run --project src/RD.Tools.Import -- "<xlsx path>" [--conn "<connection string>"] [--force]

var xlsxPath = args.FirstOrDefault(a => !a.StartsWith("--"))
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "All Clients - completed Final.xlsx");
var connIdx = Array.IndexOf(args, "--conn");
var conn = connIdx >= 0 && connIdx + 1 < args.Length
    ? args[connIdx + 1]
    : "Server=(localdb)\\MSSQLLocalDB;Database=RocketDetailers;Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True";
var force = args.Contains("--force");

var options = new DbContextOptionsBuilder<RdDbContext>()
    .UseSqlServer(conn)
    .AddInterceptors(new AppendOnlyInterceptor())
    .Options;

await using var db = new RdDbContext(options);

if (await db.Clients.AnyAsync() && !force)
{
    Console.WriteLine("Clients table is not empty. Re-run with --force to import anyway (links deduplicate via unique indexes).");
    return 1;
}

using var wb = new XLWorkbook(xlsxPath);
var ws = wb.Worksheet("All Clients");

// Headers are on row 4; data starts row 5.
var headerRow = ws.Row(4);
var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
foreach (var cell in headerRow.CellsUsed())
    headers[cell.GetString().Trim()] = cell.Address.ColumnNumber;

string Cell(IXLRow row, string name) => headers.TryGetValue(name, out var c) ? row.Cell(c).GetString().Trim() : "";

var now = DateTimeOffset.UtcNow;
var clients = 0; var links = 0; var investigations = 0; var trials = 0; var skippedEmpty = 0;
var seenLinks = new HashSet<(ExternalSystem, LinkKind, string)>();

var lastRow = ws.LastRowUsed()!.RowNumber();
await using var tx = await db.Database.BeginTransactionAsync();

for (var r = 5; r <= lastRow; r++)
{
    var row = ws.Row(r);
    var taskId = Cell(row, "Task ID");
    var taskName = Cell(row, "ClickUp Task Name");
    if (string.IsNullOrWhiteSpace(taskId) && string.IsNullOrWhiteSpace(taskName)) { skippedEmpty++; continue; }

    var businessName = Cell(row, "Client Business Name");
    if (string.IsNullOrWhiteSpace(businessName)) businessName = taskName;
    if (string.IsNullOrWhiteSpace(businessName)) { skippedEmpty++; continue; }

    var countryRaw = Cell(row, "Country of Client").ToUpperInvariant();
    var (country, currency) = countryRaw switch
    {
        "US" or "USA" => ("US", "USD"),
        "CANADA" or "CA" => ("CA", "CAD"),
        "AU" or "AUSTRALIA" => ("AU", "AUD"),
        _ => ((string?)null, "USD"),
    };

    var isTrial = Cell(row, "Contract").Equals("Trial", StringComparison.OrdinalIgnoreCase);
    var isMaster = Cell(row, "Master Ad Account?").Equals("Yes", StringComparison.OrdinalIgnoreCase);

    var client = new Client
    {
        Id = Guid.NewGuid(),
        BusinessName = Clamp(businessName, 200)!,
        ContactName = Clamp(NullIfEmpty(Cell(row, "GHL Contact Name")), 200),
        Email = CleanEmail(Cell(row, "Stripe Customer Email")),
        Phone = CleanPhone(Cell(row, "Ads Contact #")),
        Country = country,
        CurrencyCode = currency,
        ContractType = isTrial ? ContractType.Trial : ContractType.Paid,
        AccountType = isMaster ? AccountType.Master : AccountType.Own,
        EnforcementMode = EnforcementMode.Shadow,
        Notes = $"Imported from spreadsheet row {r}; lifecycle status: '{Cell(row, "Status (automatic)")}'; ClickUp status: '{Cell(row, "ClickUp Task Status")}'",
        CreatedAt = now,
    };
    db.Clients.Add(client);
    clients++;

    void AddLink(ExternalSystem system, LinkKind kind, string externalId)
    {
        if (string.IsNullOrWhiteSpace(externalId)) return;
        externalId = externalId.Trim();
        // AdAccount identities are shared (master account) — no cross-client conflict there.
        if (!seenLinks.Add((system, kind, externalId)) && kind != LinkKind.AdAccount)
        {
            db.InvestigationItems.Add(new InvestigationItem
            {
                Id = Guid.NewGuid(), ClientId = client.Id, Kind = InvestigationKind.ImportConflict,
                Detail = $"{system}/{kind} '{externalId}' already linked to another client (spreadsheet row {r}).",
                CreatedAt = now,
            });
            investigations++;
            return;
        }
        db.IdentityLinks.Add(new IdentityLink
        {
            Id = Guid.NewGuid(), ClientId = client.Id, System = system, Kind = kind,
            ExternalId = externalId, CreatedAt = now,
        });
        links++;
    }

    AddLink(ExternalSystem.ClickUp, LinkKind.Task, taskId);
    AddLink(ExternalSystem.Ghl, LinkKind.Contact, Cell(row, "GHL Contact ID"));
    AddLink(ExternalSystem.Stripe, LinkKind.Customer, Cell(row, "Stripe Customer ID (cus_...)"));
    AddLink(ExternalSystem.Stripe, LinkKind.Subscription, Cell(row, "Stripe Subscription ID (sub_...)"));
    AddLink(ExternalSystem.Meta, LinkKind.Campaign, Cell(row, "Meta Campaign ID"));
    AddLink(ExternalSystem.Meta, LinkKind.AdAccount, Cell(row, "Ad account ID"));

    // The duplicate-Stripe-customer rows: second customer/sub go to the work queue, never a second active link.
    var cus2 = Cell(row, "Stripe Customer ID 2 (cus_...)");
    var sub2 = Cell(row, "Stripe Subscription ID 2 (sub_...)");
    if (!string.IsNullOrWhiteSpace(cus2) || !string.IsNullOrWhiteSpace(sub2))
    {
        db.InvestigationItems.Add(new InvestigationItem
        {
            Id = Guid.NewGuid(), ClientId = client.Id, Kind = InvestigationKind.DuplicateStripeCustomer,
            Detail = $"Second Stripe identity in spreadsheet: customer='{cus2}', subscription='{sub2}'. Merge or invalidate.",
            CreatedAt = now,
        });
        investigations++;
    }

    if (isMaster && string.IsNullOrWhiteSpace(Cell(row, "Stripe Subscription ID (sub_...)")))
    {
        db.InvestigationItems.Add(new InvestigationItem
        {
            Id = Guid.NewGuid(), ClientId = client.Id, Kind = InvestigationKind.UnmappedIdentity,
            Detail = "Master-account client with no Stripe subscription linked — enforcement impossible until fixed.",
            CreatedAt = now,
        });
        investigations++;
    }

    if (currency != "USD")
    {
        db.InvestigationItems.Add(new InvestigationItem
        {
            Id = Guid.NewGuid(), ClientId = client.Id, Kind = InvestigationKind.NonUsdCurrency,
            Detail = $"Client bills in {currency}; v1 policy is USD-only — excluded from company exposure roll-up.",
            CreatedAt = now,
        });
        investigations++;
    }

    if (isTrial)
    {
        db.TrialPeriods.Add(new TrialPeriod
        {
            Id = Guid.NewGuid(), ClientId = client.Id, StartsAt = now, ExpiresAt = null,
        });
        trials++;
        if (isMaster)
        {
            db.InvestigationItems.Add(new InvestigationItem
            {
                Id = Guid.NewGuid(), ClientId = client.Id, Kind = InvestigationKind.MissingTrialExpiry,
                Detail = "Master-account trial with unknown expiry (OQ8) — enforcement-relevant; backfill the real trial dates.",
                CreatedAt = now,
            });
            investigations++;
        }
    }
}

await db.SaveChangesAsync();
await tx.CommitAsync();

Console.WriteLine($"Imported: {clients} clients, {links} identity links, {trials} trial periods, {investigations} investigation items. Skipped {skippedEmpty} empty rows.");
return 0;

static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

static string? Clamp(string? s, int max) => s is null ? null : (s.Length <= max ? s : s[..max]);

// The sheet's phone column contains pasted junk in places (file paths, business
// names). Keep only values with a plausible digit count; junk becomes null.
static string? CleanPhone(string s)
{
    if (string.IsNullOrWhiteSpace(s)) return null;
    var digits = s.Count(char.IsAsciiDigit);
    return digits is >= 7 and <= 15 && s.Length <= 40 ? s.Trim() : null;
}

static string? CleanEmail(string s)
{
    s = s.Trim().ToLowerInvariant();
    return s.Contains('@') && !s.Contains(' ') && s.Length <= 320 ? s : null;
}
