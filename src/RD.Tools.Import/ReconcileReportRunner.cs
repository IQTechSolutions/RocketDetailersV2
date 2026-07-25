using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using RD.Domain;
using RD.Infrastructure;

namespace RD.Tools.Import;

/// <summary>
/// READ-ONLY reconciliation coverage report. Measures how much the ClickUp Client
/// Database and the app database can be joined on EXACT external ids — ClickUp task
/// id, Stripe customer, GHL contact, Meta campaign — versus the fuzzy name matching
/// used so far. Answers: do we have a strong exact key, or are we stuck guessing by
/// name? Writes NOTHING.
///
///   dotnet run --project src/RD.Tools.Import -- reconcile-report [--list &lt;id&gt;]
/// </summary>
public static class ReconcileReportRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets(Assembly.GetExecutingAssembly(), optional: true)
            .AddEnvironmentVariables()
            .Build();

        var token = config["ClickUp:ApiToken"];
        if (string.IsNullOrWhiteSpace(token)) { Console.WriteLine("ERROR: set ClickUp:ApiToken in user-secrets."); return 1; }
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

        var clientCount = await db.Clients.CountAsync(ct);

        // App-side: active identity links → externalId ⇒ clientId, per kind.
        var taskLink = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var stripeLink = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var ghlLink = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var campaignLink = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in await db.IdentityLinks.AsNoTracking()
                     .Where(l => l.InvalidatedAt == null)
                     .Select(l => new { l.System, l.Kind, l.ExternalId, l.ClientId }).ToListAsync(ct))
        {
            if (l.System == ExternalSystem.ClickUp && l.Kind == LinkKind.Task) taskLink[l.ExternalId] = l.ClientId;
            else if (l.System == ExternalSystem.Stripe && l.Kind == LinkKind.Customer) stripeLink[l.ExternalId] = l.ClientId;
            else if (l.System == ExternalSystem.Ghl && l.Kind == LinkKind.Contact) ghlLink[l.ExternalId] = l.ClientId;
            else if (l.System == ExternalSystem.Meta && l.Kind == LinkKind.Campaign) campaignLink[l.ExternalId] = l.ClientId;
        }

        // ClickUp-side: extract the same ids per task.
        int cuStripe = 0, cuGhl = 0, cuCampaign = 0;
        int hitTask = 0, hitStripe = 0, hitGhl = 0, hitCampaign = 0;
        var reachedByTask = new HashSet<Guid>();
        var reachedByStripe = new HashSet<Guid>();
        var reachedByGhl = new HashSet<Guid>();
        var reachedByCampaign = new HashSet<Guid>();

        foreach (var t in tasks)
        {
            var stripe = StripeCustomer(t.Field("Stripe Customer ID"));
            var ghl = NullIf(t.Field("GHL CONTACT ID")) ?? ClickUpApi.GhlContactId(t.Field("GHL Contact"));
            var campaign = ClickUpApi.MetaCampaignId(t.Field("Ad Account Link"));
            if (stripe is not null) cuStripe++;
            if (ghl is not null) cuGhl++;
            if (campaign is not null) cuCampaign++;

            if (taskLink.TryGetValue(t.Id, out var c1)) { hitTask++; reachedByTask.Add(c1); }
            if (stripe is not null && stripeLink.TryGetValue(stripe, out var c2)) { hitStripe++; reachedByStripe.Add(c2); }
            if (ghl is not null && ghlLink.TryGetValue(ghl, out var c3)) { hitGhl++; reachedByGhl.Add(c3); }
            if (campaign is not null && campaignLink.TryGetValue(campaign, out var c4)) { hitCampaign++; reachedByCampaign.Add(c4); }
        }

        var reachedAny = new HashSet<Guid>(reachedByTask);
        reachedAny.UnionWith(reachedByStripe);
        reachedAny.UnionWith(reachedByGhl);
        reachedAny.UnionWith(reachedByCampaign);

        var clientsWithAnyExactId = new HashSet<Guid>(taskLink.Values);
        clientsWithAnyExactId.UnionWith(stripeLink.Values);
        clientsWithAnyExactId.UnionWith(ghlLink.Values);
        clientsWithAnyExactId.UnionWith(campaignLink.Values);

        Console.WriteLine($"[reconcile-report] list={listId}  (read-only)\n");
        Console.WriteLine($"App clients: {clientCount}   ClickUp tasks: {tasks.Count}\n");

        Console.WriteLine("=== App-side identity coverage (active links) ===");
        Console.WriteLine($"  ClickUp/Task links:    {taskLink.Count}");
        Console.WriteLine($"  Stripe/Customer links: {stripeLink.Count}");
        Console.WriteLine($"  Ghl/Contact links:     {ghlLink.Count}");
        Console.WriteLine($"  Meta/Campaign links:   {campaignLink.Count}");
        Console.WriteLine($"  clients with ANY exact id: {clientsWithAnyExactId.Count} / {clientCount}  (the rest are name-only)\n");

        Console.WriteLine("=== ClickUp-side id coverage ===");
        Console.WriteLine($"  tasks with Stripe Customer ID (cus_): {cuStripe}");
        Console.WriteLine($"  tasks with a GHL contact id:          {cuGhl}");
        Console.WriteLine($"  tasks with a Meta campaign id:        {cuCampaign}");
        Console.WriteLine($"  (every task has a ClickUp task id: {tasks.Count})\n");

        Console.WriteLine("=== Exact-ID join: ClickUp task ↔ app client ===");
        Console.WriteLine($"  by ClickUp Task ID:  {hitTask} task hits → {reachedByTask.Count} distinct clients");
        Console.WriteLine($"  by Stripe customer:  {hitStripe} task hits → {reachedByStripe.Count} distinct clients");
        Console.WriteLine($"  by GHL contact:      {hitGhl} task hits → {reachedByGhl.Count} distinct clients");
        Console.WriteLine($"  by Meta campaign:    {hitCampaign} task hits → {reachedByCampaign.Count} distinct clients");
        Console.WriteLine($"  ── distinct clients reachable by ANY exact id: {reachedAny.Count} / {clientCount}");
        Console.WriteLine($"     (compare: 393 reached by name/email in the last run)\n");

        Console.WriteLine("[reconcile-report] done — read-only, nothing written.");
        return 0;
    }

    private static string? StripeCustomer(string? raw)
        => raw is not null && raw.Trim().StartsWith("cus_", StringComparison.OrdinalIgnoreCase) ? raw.Trim() : null;

    private static string? NullIf(string? raw) => string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

    private static string? ArgValue(string[] args, string flag)
    {
        var i = Array.FindIndex(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
