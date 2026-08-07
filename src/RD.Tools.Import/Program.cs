using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using RD.Domain;
using RD.Domain.Entities;
using RD.Infrastructure;
using RD.Infrastructure.Enforcement;
using RD.Tools.Import;

// "sync" verb: one-shot vendor sync + policy evaluation (see SyncRunner).
if (args.Length > 0 && args[0].Equals("sync", StringComparison.OrdinalIgnoreCase))
    return await SyncRunner.RunAsync(args);

// "seed-user" verb: create/reset an admin directly (see SeedUserRunner).
if (args.Length > 0 && args[0].Equals("seed-user", StringComparison.OrdinalIgnoreCase))
    return await SeedUserRunner.RunAsync(args);

// "discover" verb: read-only Stripe sweep → candidate client roster (see StripeDiscoveryRunner).
if (args.Length > 0 && args[0].Equals("discover", StringComparison.OrdinalIgnoreCase))
    return await StripeDiscoveryRunner.RunAsync(args);

// "link-meta" verb: read-only Meta sweep → attach campaigns to clients via the sheet crosswalk (see MetaLinkRunner).
if (args.Length > 0 && args[0].Equals("link-meta", StringComparison.OrdinalIgnoreCase))
    return await MetaLinkRunner.RunAsync(args);

// "link-ghl" verb: link GHL contacts to clients from the sheet crosswalk (no GHL API — see GhlLinkRunner).
if (args.Length > 0 && args[0].Equals("link-ghl", StringComparison.OrdinalIgnoreCase))
    return await GhlLinkRunner.RunAsync(args);

// "link-ghl-live" verb: search all GHL locations by email/phone/name to link the rest (see LinkGhlLiveRunner).
if (args.Length > 0 && args[0].Equals("link-ghl-live", StringComparison.OrdinalIgnoreCase))
    return await LinkGhlLiveRunner.RunAsync(args);

// "mark-master" verb: set AccountType=Master from the sheet's "Master Ad Account?" column (see MarkMasterRunner).
if (args.Length > 0 && args[0].Equals("mark-master", StringComparison.OrdinalIgnoreCase))
    return await MarkMasterRunner.RunAsync(args);

// "infer-arrangements" verb: infer each client's payment cadence/amount from history (see ArrangementInferenceRunner).
if (args.Length > 0 && args[0].Equals("infer-arrangements", StringComparison.OrdinalIgnoreCase))
    return await ArrangementInferenceRunner.RunAsync(args);

// "discover-clickup" verb: read-only ClickUp probe → custom-field catalog + trial cross-check (see ClickUpDiscoveryRunner).
if (args.Length > 0 && args[0].Equals("discover-clickup", StringComparison.OrdinalIgnoreCase))
    return await ClickUpDiscoveryRunner.RunAsync(args);

// "mark-trials" verb: match ClickUp's active-trial cohort to EXISTING clients (email/phone/name) and mark them Trial — never creates clients (see ClickUpTrialImportRunner). Dry-run unless --commit.
if (args.Length > 0 && args[0].Equals("mark-trials", StringComparison.OrdinalIgnoreCase))
    return await ClickUpTrialImportRunner.RunAsync(args);

// "link-meta-clickup" verb: match existing clients to their Meta ad account / campaign from ClickUp fields (see LinkMetaClickUpRunner). Dry-run unless --commit.
if (args.Length > 0 && args[0].Equals("link-meta-clickup", StringComparison.OrdinalIgnoreCase))
    return await LinkMetaClickUpRunner.RunAsync(args);

// "reconcile-report" verb: read-only exact-id overlap report between ClickUp and the app DB (see ReconcileReportRunner).
if (args.Length > 0 && args[0].Equals("reconcile-report", StringComparison.OrdinalIgnoreCase))
    return await ReconcileReportRunner.RunAsync(args);

// "match-report" verb: read-only GHL-anchored exact-id matcher vs name matching — agreement/disagreement + backfill sizing (see MatchReportRunner).
if (args.Length > 0 && args[0].Equals("match-report", StringComparison.OrdinalIgnoreCase))
    return await MatchReportRunner.RunAsync(args);

// "backfill-phones" verb: write ClickUp phones onto exact-id-matched clients that have none (see BackfillPhonesRunner). Dry-run unless --commit.
if (args.Length > 0 && args[0].Equals("backfill-phones", StringComparison.OrdinalIgnoreCase))
    return await BackfillPhonesRunner.RunAsync(args);

// "surface-review" verb: raise investigations for name/id disagreements + name-only matches (see SurfaceReviewRunner). Dry-run unless --commit.
if (args.Length > 0 && args[0].Equals("surface-review", StringComparison.OrdinalIgnoreCase))
    return await SurfaceReviewRunner.RunAsync(args);

// "backfill-adspend" verb: pull historical Meta ad spend into the ledger for linked campaigns (see BackfillAdSpendRunner). Dry-run unless --commit.
if (args.Length > 0 && args[0].Equals("backfill-adspend", StringComparison.OrdinalIgnoreCase))
    return await BackfillAdSpendRunner.RunAsync(args);

// "scorecard" verb: read-only post-reconciliation scorecard (client mix, links, net cash, investigations) — see ScorecardRunner.
if (args.Length > 0 && args[0].Equals("scorecard", StringComparison.OrdinalIgnoreCase))
    return await ScorecardRunner.RunAsync(args);

// Seed importer: one-time load of the reconciliation spreadsheet into SQL.
// Disposable by design (design doc: "one-time spreadsheet import code").
// Usage: dotnet run --project src/RD.Tools.Import -- "<xlsx path>" [--conn "<connection string>"] [--force] [--activate-secondary-stripe-links]

var xlsxPath = args.FirstOrDefault(a => !a.StartsWith("--"))
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "All Clients - completed Final.xlsx");
var connIdx = Array.IndexOf(args, "--conn");
var conn = connIdx >= 0 && connIdx + 1 < args.Length
    ? args[connIdx + 1]
    : "Server=(localdb)\\MSSQLLocalDB;Database=RocketDetailers;Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True";
var force = args.Contains("--force");
var activateSecondaryStripeLinks = args.Contains(
    "--activate-secondary-stripe-links",
    StringComparer.OrdinalIgnoreCase);

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
await using var ownershipFence =
    await ClientMutationFence.AcquireMappingOwnershipAsync(db);
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
                System = system, ExternalId = externalId,
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

    // A second Stripe identity needs a human to confirm that it belongs to the
    // same business (then link/monitor it); separate-business cases stay open
    // for manual mapping correction outside this workflow.
    var cus2 = Cell(row, "Stripe Customer ID 2 (cus_...)");
    var sub2 = Cell(row, "Stripe Subscription ID 2 (sub_...)");
    // Phase-one deployments deliberately leave secondary identities hidden so
    // the previous arbitrary-first billing code remains a safe rollback target.
    // Activate only after the preference-aware binary is the established
    // baseline (the phase-two startup repair uses the same staged boundary).
    if (activateSecondaryStripeLinks)
    {
        AddLink(ExternalSystem.Stripe, LinkKind.Customer, cus2);
        AddLink(ExternalSystem.Stripe, LinkKind.Subscription, sub2);
    }
    if (!string.IsNullOrWhiteSpace(cus2) || !string.IsNullOrWhiteSpace(sub2))
    {
        db.InvestigationItems.Add(new InvestigationItem
        {
            Id = Guid.NewGuid(), ClientId = client.Id, Kind = InvestigationKind.DuplicateStripeCustomer,
            Detail = $"Second Stripe identity in spreadsheet: customer='{cus2}', subscription='{sub2}'. Confirm the same business here; otherwise leave open for manual mapping correction.",
            System = ExternalSystem.Stripe, ExternalId = string.IsNullOrWhiteSpace(cus2) ? null : cus2,
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
