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
using RD.Infrastructure.Reconciliation;
using RD.Infrastructure.Sync;

namespace RD.Tools.Import;

/// <summary>
/// Read-only Stripe discovery → a CLUSTERED candidate roster for one-at-a-time triage.
///
///   dotnet run --project src/RD.Tools.Import -- discover [--commit] [--active-only] [--crosswalk &lt;xlsx&gt;]
///
/// Sweeps live Stripe (read-only), clusters customers into candidate businesses
/// via <see cref="CustomerClusterer"/> (shared normalized name OR shared email —
/// so one business scattered across several customer records + emails collapses
/// to ONE candidate), and lands one <see cref="Client"/> per cluster in
/// <see cref="EnforcementMode.Shadow"/>. Every member customer + subscription is
/// linked; every human-judgment exception becomes an <see cref="InvestigationItem"/>.
///
/// The CUSTOMER NAME is the client's identity and the reconciliation key; the
/// sheet's business-name column is a CATEGORY label only (carried in Notes), never
/// the identity. Clustering NEVER auto-decides: a multi-member cluster is persisted
/// pre-grouped but carries a same-business confirmation investigation, because a shared
/// normalized customer name can genuinely belong to two different clients. The
/// --crosswalk sheet ("All Clients" master) maps Stripe customer id → category
/// (and, in the link verbs, → Meta campaign / GHL contact / Master flag).
///
/// Dry-run by default. Credentials: Stripe:ApiKey from user-secrets or env
/// (Stripe__ApiKey). Connection: RD_CONN, or ConnectionStrings:RocketDetailers.
/// Idempotent: a cluster any of whose customers is already linked is skipped.
/// </summary>
public static class StripeDiscoveryRunner
{
    private const string DefaultCrosswalkName = "All Clients - completed Final.xlsx";
    public const InvestigationKind DelinquentInvestigationKind = InvestigationKind.StripeCustomerDelinquent;

    public static async Task<int> RunAsync(string[] args)
    {
        var commit = args.Any(a => a.Equals("--commit", StringComparison.OrdinalIgnoreCase));
        // --active-only: skip clusters with no subscription at all (Stripe keeps a
        // customer record for every checkout, incl. one-time/guest buyers who were
        // never recurring clients). This scopes the roster to real subscription-holders.
        var activeOnly = args.Any(a => a.Equals("--active-only", StringComparison.OrdinalIgnoreCase));
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
        if (string.IsNullOrWhiteSpace(config["Stripe:ApiKey"]))
        {
            Console.WriteLine("ERROR: no Stripe API key configured. Set it once (stored outside the repo):");
            Console.WriteLine("  dotnet user-secrets set \"Stripe:ApiKey\" \"<rk_live_...>\" --project src/RD.Tools.Import");
            return 1;
        }

        var services = new ServiceCollection();
        services.AddLogging(l => l.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IConfiguration>(config);
        services.AddDbContextFactory<RdDbContext>(o => o.UseSqlServer(conn).AddInterceptors(new AppendOnlyInterceptor()));
        services.AddRdSync(config);

        await using var provider = services.BuildServiceProvider();
        var stripe = provider.GetRequiredService<IStripeGateway>();
        var factory = provider.GetRequiredService<IDbContextFactory<RdDbContext>>();
        var ct = CancellationToken.None;

        Console.WriteLine($"[discover] mode={(commit ? "COMMIT" : "DRY-RUN")}  target={Mask(conn)}");

        // Optional deterministic person→business bridge from the master sheet.
        var crosswalk = LoadCrosswalk(crosswalkPath);
        if (crosswalkPath is not null)
            Console.WriteLine($"[discover] crosswalk: {crosswalk.Count} Stripe-customer→category mappings from {Path.GetFileName(crosswalkPath)}.");
        else
            Console.WriteLine("[discover] crosswalk: none (no category labels — client identity is always the customer name).");

        Console.WriteLine("[discover] pulling live Stripe customers + subscriptions (read-only)…");
        IReadOnlyList<StripeCustomerDto> customers;
        IReadOnlyList<StripeSubscriptionDto> subs;
        try
        {
            customers = await stripe.ListCustomersAsync(ct);
            subs = await stripe.ListSubscriptionsAsync(ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[discover] Stripe read FAILED: {ex.Message}");
            Console.WriteLine("[discover] Check the API key scope (needs read) and Stripe:ApiVersion (leave unset = account default).");
            return 1;
        }

        var subsByCustomer = subs.Where(s => s.CustomerId.Length > 0)
            .GroupBy(s => s.CustomerId).ToDictionary(g => g.Key, g => g.ToList());

        var clusters = CustomerClusterer.Cluster(customers);
        Console.WriteLine($"[discover] Stripe: {customers.Count} customers, {subs.Count} subscriptions → {clusters.Count} candidate businesses " +
                          $"({clusters.Count(c => c.Members.Count > 1)} are multi-customer clusters).");

        await using var db = await factory.CreateDbContextAsync(ct);
        var alreadyLinked = (await db.IdentityLinks.AsNoTracking()
                .Where(l => l.System == ExternalSystem.Stripe && l.Kind == LinkKind.Customer)
                .Select(l => l.ExternalId).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var now = DateTimeOffset.UtcNow;
        int created = 0, skipped = 0, filteredInactive = 0, singles = 0, multi = 0, custLinks = 0, subLinks = 0, trials = 0, flags = 0;
        int noSub = 0, delinquent = 0, nonUsd = 0, fromSheet = 0;

        foreach (var cluster in clusters)
        {
            var members = cluster.Members;
            if (members.Any(m => alreadyLinked.Contains(m.Id))) { skipped++; continue; }

            var memberSubs = members.SelectMany(m => subsByCustomer.TryGetValue(m.Id, out var l) ? l : []).ToList();
            if (activeOnly && memberSubs.Count == 0) { filteredInactive++; continue; }
            var anchorSub = memberSubs.FirstOrDefault(s => s.Status is "active" or "trialing" or "past_due") ?? memberSubs.FirstOrDefault();
            var currency = (anchorSub?.CurrencyCode ?? members.Select(m => m.CurrencyCode).FirstOrDefault(c => c is not null) ?? "USD").ToUpperInvariant();
            var isTrial = memberSubs.Any(s => s.Status == "trialing");
            var email = members.Select(m => m.Email).FirstOrDefault(e => e is not null);
            var country = members.Select(m => m.Country).FirstOrDefault(c => c is { Length: <= 2 });

            // The CUSTOMER NAME is the client's identity and the reconciliation key
            // (owner-confirmed). The sheet's business-name column is a CATEGORY label,
            // not the identity — carry it in Notes, never as the client name.
            var category = members.Select(m => crosswalk.TryGetValue(m.Id, out var b) ? b : null).FirstOrDefault(b => b is not null);
            if (category is not null) fromSheet++;

            var client = new Client
            {
                Id = Guid.NewGuid(),
                BusinessName = Clamp(cluster.DisplayName, 200)!, // customer name = the client identity + join key
                ContactName = Clamp(cluster.DisplayName, 200),
                Email = email,
                Country = country,
                CurrencyCode = currency,
                ContractType = isTrial ? ContractType.Trial : ContractType.Paid,
                AccountType = AccountType.Own, // Master is a human call — never inferred.
                EnforcementMode = EnforcementMode.Shadow,
                Notes = $"Discovered from live Stripe {now:yyyy-MM-dd}. {members.Count} Stripe customer(s): " +
                        $"{string.Join(", ", members.Take(12).Select(m => m.Id))}{(members.Count > 12 ? $" (+{members.Count - 12} more)" : "")}." +
                        (category is not null ? $" Category: {category}." : ""),
                CreatedAt = now,
            };
            db.Clients.Add(client);
            created++;
            if (members.Count > 1) multi++; else singles++;

            foreach (var m in members)
            {
                db.IdentityLinks.Add(new IdentityLink
                {
                    Id = Guid.NewGuid(), ClientId = client.Id, System = ExternalSystem.Stripe,
                    Kind = LinkKind.Customer, ExternalId = m.Id, CreatedAt = now,
                });
                custLinks++;
            }
            foreach (var s in memberSubs)
            {
                db.IdentityLinks.Add(new IdentityLink
                {
                    Id = Guid.NewGuid(), ClientId = client.Id, System = ExternalSystem.Stripe,
                    Kind = LinkKind.Subscription, ExternalId = s.Id, CreatedAt = now,
                });
                subLinks++;
            }

            void Flag(InvestigationKind kind, string detail)
            {
                // Detail is capped at 1000 chars in the schema — clamp defensively so a
                // big cluster's roster can never fail the whole batch on truncation.
                if (detail.Length > 1000) detail = detail[..997] + "...";
                db.InvestigationItems.Add(new InvestigationItem
                {
                    Id = Guid.NewGuid(), ClientId = client.Id, Kind = kind, Detail = detail, CreatedAt = now,
                });
                flags++;
            }

            // The one-at-a-time task: confirm a pre-grouped cluster. Separate
            // businesses remain open for manual mapping correction.
            if (members.Count > 1)
            {
                var roster = string.Join("; ", members.Take(12).Select(m => $"{m.Id} <{m.Email ?? "no email"}>"))
                             + (members.Count > 12 ? $" (+{members.Count - 12} more)" : "");
                Flag(InvestigationKind.DuplicateStripeCustomer,
                    $"CONFIRM SAME BUSINESS — {members.Count} Stripe customers grouped together [{cluster.Confidence}]. " +
                    "If they are separate businesses, leave this open for manual mapping correction. " +
                    $"Signals: {(cluster.Signals.Count > 0 ? string.Join("; ", cluster.Signals) : "n/a")}. Members: {roster}.");
            }

            if (memberSubs.Count == 0) { Flag(InvestigationKind.UnmappedIdentity, "No subscription on any customer in this cluster — inactive, prospect, or stray account?"); noSub++; }
            if (members.Any(m => m.Delinquent)) { Flag(DelinquentInvestigationKind, "At least one Stripe customer here is delinquent (unpaid invoice)."); delinquent++; }
            if (currency != "USD") { Flag(InvestigationKind.NonUsdCurrency, $"Bills in {currency}; v1 policy is USD-only."); nonUsd++; }
            if (isTrial)
            {
                db.TrialPeriods.Add(new TrialPeriod { Id = Guid.NewGuid(), ClientId = client.Id, StartsAt = now, ExpiresAt = null });
                trials++;
                Flag(InvestigationKind.MissingTrialExpiry, "Trialing subscription — backfill the real trial expiry.");
            }
        }

        Console.WriteLine($"[discover] candidates: {created} new ({singles} single, {multi} multi-customer), {skipped} skipped (already linked)" +
                          (activeOnly ? $", {filteredInactive} filtered (no subscription)." : "."));
        Console.WriteLine($"[discover] links: {custLinks} Stripe customers, {subLinks} subscriptions.  trials: {trials}.  categories from sheet: {fromSheet}.");
        Console.WriteLine($"[discover] flags: {flags} total  —  cluster-to-confirm:{multi}  no-subscription:{noSub}  delinquent:{delinquent}  non-USD:{nonUsd}");

        if (commit)
        {
            await db.SaveChangesAsync(ct);
            Console.WriteLine($"[discover] COMMITTED {created} candidate businesses (all Shadow — nothing enforces).");
        }
        else
        {
            Console.WriteLine("[discover] DRY-RUN — nothing written. Re-run with --commit to persist.");
        }
        return 0;
    }

    private static string? ResolveCrosswalkPath(string[] args)
    {
        var idx = Array.FindIndex(args, a => a.Equals("--crosswalk", StringComparison.OrdinalIgnoreCase));
        if (idx >= 0 && idx + 1 < args.Length)
            return File.Exists(args[idx + 1]) ? args[idx + 1] : null;
        if (args.Any(a => a.Equals("--no-crosswalk", StringComparison.OrdinalIgnoreCase)))
            return null;
        var def = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", DefaultCrosswalkName);
        return File.Exists(def) ? def : null;
    }

    /// <summary>Stripe customer id → category label, from the master sheet (cols "Stripe Customer ID"/"...ID 2" → "Client Business Name", used as a category, not the identity).</summary>
    private static Dictionary<string, string> LoadCrosswalk(string? path)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (path is null) return map;
        try
        {
            using var wb = new XLWorkbook(path);
            var ws = wb.Worksheet("All Clients");
            var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var cell in ws.Row(4).CellsUsed()) headers[cell.GetString().Trim()] = cell.Address.ColumnNumber;
            int? Col(string sub) => headers.Where(kv => kv.Key.Contains(sub, StringComparison.OrdinalIgnoreCase))
                .Select(kv => (int?)kv.Value).FirstOrDefault();

            var bizCol = Col("Client Business Name");
            var idCols = new[] { Col("Stripe Customer ID (cus"), Col("Stripe Customer ID 2") }.Where(c => c is not null).ToList();
            if (bizCol is null || idCols.Count == 0) return map;

            var last = ws.LastRowUsed()!.RowNumber();
            for (var r = 5; r <= last; r++)
            {
                var biz = ws.Row(r).Cell(bizCol.Value).GetString().Trim();
                if (biz.Length == 0) continue;
                foreach (var c in idCols)
                {
                    var id = ws.Row(r).Cell(c!.Value).GetString().Trim();
                    if (id.StartsWith("cus_", StringComparison.OrdinalIgnoreCase)) map[id] = biz;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[discover] crosswalk load skipped ({ex.GetType().Name}: {ex.Message}).");
        }
        return map;
    }

    private static string? Clamp(string? s, int max) => s is null ? null : (s.Length <= max ? s : s[..max]);

    private static string Mask(string conn) => Regex.Replace(conn, "(?i)(password|pwd)=[^;]*", "$1=***");
}
