using Microsoft.EntityFrameworkCore;
using RD.Domain;
using RD.Infrastructure;

namespace RD.Tools.Import;

/// <summary>
/// The shared ClickUp→client matcher. Resolves a task to an app client by EXACT id
/// first — GHL contact → Stripe customer → Meta campaign, all unique-per-client, so
/// unambiguous and high-confidence — then falls back to fuzzy email → phone → name.
/// Returns the client, the signal that hit, and whether it was an exact id.
/// Built once from the DB (projections only) and reused across the reconciliation runners.
/// </summary>
public sealed class ClickUpMatchIndex
{
    private readonly Dictionary<string, Guid> _ghl = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Guid> _stripe = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Guid> _campaign = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<Guid>> _byEmail = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<Guid>> _byPhone = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<Guid>> _byName = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<ClickUpMatchIndex> BuildAsync(RdDbContext db, CancellationToken ct)
    {
        var ix = new ClickUpMatchIndex();
        foreach (var l in await db.IdentityLinks.AsNoTracking()
                     .Where(l => l.InvalidatedAt == null)
                     .Select(l => new { l.System, l.Kind, l.ExternalId, l.ClientId }).ToListAsync(ct))
        {
            if (l.System == ExternalSystem.Ghl && l.Kind == LinkKind.Contact) ix._ghl[l.ExternalId] = l.ClientId;
            else if (l.System == ExternalSystem.Stripe && l.Kind == LinkKind.Customer) ix._stripe[l.ExternalId] = l.ClientId;
            else if (l.System == ExternalSystem.Meta && l.Kind == LinkKind.Campaign) ix._campaign[l.ExternalId] = l.ClientId;
        }
        foreach (var c in await db.Clients.AsNoTracking()
                     .Select(c => new { c.Id, c.BusinessName, c.ContactName, c.Email, c.Phone }).ToListAsync(ct))
        {
            Add(ix._byEmail, NormEmail(c.Email), c.Id);
            Add(ix._byPhone, PhoneTail(c.Phone), c.Id);
            Add(ix._byName, NameNormalizer.Normalize(c.BusinessName), c.Id);
            Add(ix._byName, NameNormalizer.Normalize(c.ContactName), c.Id);
        }
        return ix;
    }

    /// <summary>Best-effort resolution: (client, signal, wasExactId). Exact id first, then fuzzy.</summary>
    public (Guid? ClientId, string Signal, bool Exact) Resolve(ClickUpTask t)
    {
        var (ex, exSig) = ResolveExact(t);
        if (ex is not null) return (ex, exSig, true);
        var (nm, nmSig) = ResolveName(t);
        return (nm, nm is null ? "none" : nmSig, false);
    }

    /// <summary>Exact, unambiguous id match only (GHL → Stripe → campaign), or null.</summary>
    public (Guid? ClientId, string Signal) ResolveExact(ClickUpTask t)
    {
        var ghlId = NullIf(t.Field("GHL CONTACT ID")) ?? ClickUpApi.GhlContactId(t.Field("GHL Contact"));
        if (ghlId is not null && _ghl.TryGetValue(ghlId, out var g)) return (g, "ghl");

        var cus = t.Field("Stripe Customer ID");
        if (cus is not null && cus.Trim().StartsWith("cus_", StringComparison.OrdinalIgnoreCase) && _stripe.TryGetValue(cus.Trim(), out var s))
            return (s, "stripe");

        var camp = ClickUpApi.MetaCampaignId(t.Field("Ad Account Link"));
        if (camp is not null && _campaign.TryGetValue(camp, out var c)) return (c, "campaign");

        return (null, "none");
    }

    /// <summary>Fuzzy email → phone → name match (single unambiguous client), or null.</summary>
    public (Guid? ClientId, string Signal) ResolveName(ClickUpTask t)
    {
        if (TaskEmail(t) is { } em && _byEmail.TryGetValue(em, out var eset) && eset.Count == 1) return (eset.First(), "email");
        if (PhoneTail(t.Field("Ads Contact #")) is { } ph && _byPhone.TryGetValue(ph, out var pset) && pset.Count == 1) return (pset.First(), "phone");

        var names = new[] { NameNormalizer.Normalize(t.Field("Business Name")), NameNormalizer.Normalize(t.Name) }
            .Where(n => n.Length > 0).Distinct();
        var hits = new HashSet<Guid>();
        foreach (var n in names) if (_byName.TryGetValue(n, out var nset)) hits.UnionWith(nset);
        return hits.Count == 1 ? (hits.First(), "name") : (null, "none");
    }

    private static void Add(Dictionary<string, HashSet<Guid>> ix, string? key, Guid id)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        (ix.TryGetValue(key, out var set) ? set : ix[key] = new HashSet<Guid>()).Add(id);
    }

    public static string? TaskEmail(ClickUpTask t) => NormEmail(t.Field("Email")) ?? NormEmail(t.Field("stripe email/link"));

    public static string? NormEmail(string? raw)
    {
        var s = raw?.Trim().ToLowerInvariant();
        return !string.IsNullOrEmpty(s) && s.Contains('@') && !s.Contains(' ') ? s : null;
    }

    public static string? PhoneTail(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var d = new string(raw.Where(char.IsDigit).ToArray());
        return d.Length >= 10 ? d[^10..] : null;
    }

    public static string? NullIf(string? raw) => string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
}
