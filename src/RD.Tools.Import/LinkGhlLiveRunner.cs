using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RD.Domain;
using RD.Domain.Entities;
using RD.Infrastructure;
using RD.Infrastructure.Gateways;
using RD.Infrastructure.Sync;

namespace RD.Tools.Import;

/// <summary>
/// Live GHL contact matching. Every client should have a GHL contact (it is the
/// comms channel for private AND master clients), so for each client with none
/// linked, search ALL configured GHL locations and match on email → phone → name
/// (in that confidence order). Uses each location's Private Integration Token
/// (Ghl:Locations:N:Token, optional :LocationId). Read-only against GHL;
/// idempotent; a contact already linked to another client is left alone.
///
///   dotnet run --project src/RD.Tools.Import -- link-ghl-live [--commit]
/// </summary>
public static class LinkGhlLiveRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        var commit = args.Any(a => a.Equals("--commit", StringComparison.OrdinalIgnoreCase));

        var config = new ConfigurationBuilder()
            .AddUserSecrets(Assembly.GetExecutingAssembly(), optional: true)
            .AddEnvironmentVariables()
            .Build();

        var conn = Environment.GetEnvironmentVariable("RD_CONN") ?? config.GetConnectionString("RocketDetailers");
        if (string.IsNullOrWhiteSpace(conn)) { Console.WriteLine("ERROR: set the RD_CONN environment variable."); return 1; }

        var services = new ServiceCollection();
        services.AddLogging(l => l.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IConfiguration>(config);
        services.AddDbContextFactory<RdDbContext>(o => o.UseSqlServer(conn).AddInterceptors(new AppendOnlyInterceptor()));
        services.AddRdSync(config);

        await using var provider = services.BuildServiceProvider();
        var ghl = provider.GetRequiredService<IGhlGateway>();
        var locations = provider.GetRequiredService<IOptions<GhlOptions>>().Value.Locations
            .Where(l => !string.IsNullOrWhiteSpace(l.Token)).ToList();
        var factory = provider.GetRequiredService<IDbContextFactory<RdDbContext>>();
        var ct = CancellationToken.None;

        if (locations.Count == 0)
        {
            Console.WriteLine("ERROR: no GHL locations configured. Set each key (repeat :0: :1: :2: for all three):");
            Console.WriteLine("  dotnet user-secrets set \"Ghl:Locations:0:Token\" \"<pit-key>\" --project src/RD.Tools.Import");
            Console.WriteLine("  dotnet user-secrets set \"Ghl:Locations:0:LocationId\" \"<location-id>\" --project src/RD.Tools.Import   (optional but recommended)");
            return 1;
        }
        Console.WriteLine($"[link-ghl-live] mode={(commit ? "COMMIT" : "DRY-RUN")}  locations={locations.Count}");

        await using var db = await factory.CreateDbContextAsync(ct);

        var contactOwner = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var clientsWithContact = new HashSet<Guid>();
        foreach (var l in await db.IdentityLinks.AsNoTracking()
                     .Where(l => l.System == ExternalSystem.Ghl && l.Kind == LinkKind.Contact && l.InvalidatedAt == null)
                     .Select(l => new { l.ClientId, l.ExternalId }).ToListAsync(ct))
        {
            contactOwner[l.ExternalId] = l.ClientId;
            clientsWithContact.Add(l.ClientId);
        }

        var candidates = await db.Clients
            .Where(c => !clientsWithContact.Contains(c.Id))
            .Select(c => new { c.Id, c.Email, c.Phone, c.BusinessName })
            .ToListAsync(ct);
        Console.WriteLine($"[link-ghl-live] {candidates.Count} clients have no GHL contact — searching each across {locations.Count} location(s) by email → phone → name…");

        var now = DateTimeOffset.UtcNow;
        int matched = 0, notFound = 0, conflicts = 0, searched = 0;
        var bySignal = new Dictionary<string, int> { ["email"] = 0, ["phone"] = 0, ["name"] = 0 };

        foreach (var client in candidates)
        {
            (string contactId, string signal)? hit = null;
            foreach (var loc in locations)
            {
                hit = await MatchInLocation(ghl, loc, client.Email, client.Phone, client.BusinessName, ct);
                if (hit is not null) break;
            }
            if (++searched % 100 == 0) Console.WriteLine($"[link-ghl-live] …{searched}/{candidates.Count} searched, {matched} matched.");

            if (hit is null) { notFound++; continue; }
            if (contactOwner.TryGetValue(hit.Value.contactId, out var owner) && owner != client.Id) { conflicts++; continue; }

            db.IdentityLinks.Add(new IdentityLink
            {
                Id = Guid.NewGuid(), ClientId = client.Id, System = ExternalSystem.Ghl,
                Kind = LinkKind.Contact, ExternalId = hit.Value.contactId, CreatedAt = now,
            });
            contactOwner[hit.Value.contactId] = client.Id;
            matched++;
            bySignal[hit.Value.signal]++;
        }

        Console.WriteLine($"[link-ghl-live] matched {matched} (email:{bySignal["email"]}, phone:{bySignal["phone"]}, name:{bySignal["name"]}).  no match anywhere: {notFound}.  already-linked-elsewhere: {conflicts}.");

        if (commit)
        {
            await db.SaveChangesAsync(ct);
            Console.WriteLine($"[link-ghl-live] COMMITTED {matched} GHL contact links.");
        }
        else
        {
            Console.WriteLine("[link-ghl-live] DRY-RUN — nothing written. Re-run with --commit to persist.");
        }
        return 0;
    }

    /// <summary>Email (exact) → phone (last-10 digits) → name (unique normalized). Returns the contact id + which signal matched.</summary>
    private static async Task<(string contactId, string signal)?> MatchInLocation(
        IGhlGateway ghl, GhlLocationOptions loc, string? email, string? phone, string? name, CancellationToken ct)
    {
        var locId = string.IsNullOrWhiteSpace(loc.LocationId) ? null : loc.LocationId;

        if (!string.IsNullOrWhiteSpace(email))
        {
            try
            {
                var byEmail = await ghl.SearchContactsAsync(locId, loc.Token, email, ct);
                var m = byEmail.FirstOrDefault(c => string.Equals(c.Email, email, StringComparison.OrdinalIgnoreCase));
                if (m is not null) return (m.Id, "email");
            }
            catch { /* try the next signal / location */ }
        }

        var phoneTail = DigitsTail(phone);
        if (phoneTail.Length >= 7)
        {
            try
            {
                var byPhone = await ghl.SearchContactsAsync(locId, loc.Token, phoneTail, ct);
                var m = byPhone.FirstOrDefault(c => DigitsTail(c.Phone) == phoneTail);
                if (m is not null) return (m.Id, "phone");
            }
            catch { }
        }

        var normName = NameNormalizer.Normalize(name);
        if (normName.Length > 0)
        {
            try
            {
                var byName = await ghl.SearchContactsAsync(locId, loc.Token, name!, ct);
                var exact = byName.Where(c => NameNormalizer.Normalize(c.Name) == normName).ToList();
                if (exact.Count == 1) return (exact[0].Id, "name"); // only an UNAMBIGUOUS name match
            }
            catch { }
        }

        return null;
    }

    private static string DigitsTail(string? s)
    {
        var digits = new string((s ?? "").Where(char.IsDigit).ToArray());
        return digits.Length > 10 ? digits[^10..] : digits;
    }
}
