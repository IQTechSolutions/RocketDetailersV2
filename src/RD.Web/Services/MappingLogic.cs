using System.Text;
using RD.Domain;

namespace RD.Web.Services;

/// <summary>
/// The four enforcement-critical links every master-account client must have,
/// active and verified, before the engine may leave Shadow. Order is the order
/// the wizard renders the slots.
/// </summary>
public static class RequiredLinks
{
    public sealed record Spec(ExternalSystem System, LinkKind Kind, string Label, string HelpText);

    public static readonly IReadOnlyList<Spec> All =
    [
        new(ExternalSystem.Stripe, LinkKind.Customer, "Stripe customer",
            "The billing account this business pays from — anchors every invoice and charge to the right client."),
        new(ExternalSystem.Stripe, LinkKind.Subscription, "Stripe subscription",
            "The recurring plan that funds the ads. Its status is exactly what the engine watches to pause or resume."),
        new(ExternalSystem.Meta, LinkKind.Campaign, "Meta campaign",
            "The ad campaign the engine can pause. This is the lever — link the wrong one and the wrong client's ads stop."),
        new(ExternalSystem.Ghl, LinkKind.Contact, "GoHighLevel contact",
            "The contact that receives dunning messages when a payment fails."),
    ];

    public static bool IsRequired(ExternalSystem system, LinkKind kind)
        => All.Any(s => s.System == system && s.Kind == kind);
}

/// <summary>
/// Pure promotion guard — the single source of truth for "may this client leave
/// Shadow?". The wizard promotes only to Assist; Auto is earned by clean days,
/// never by a button. Tested directly (no DB) so PromoteToAssist's refusal is
/// pinned in a unit test.
/// </summary>
public static class PromotionGuard
{
    public static (bool CanPromote, string Reason) CanPromote(bool hasCurrentVerification, bool requiredLinksPresent)
    {
        if (!requiredLinksPresent)
            return (false, "All four required links must be present and active before this client can leave Shadow.");
        if (!hasCurrentVerification)
            return (false, "No current mapping verification — verify the mapping first. Assist is earned by a verified mapping, never granted directly.");
        return (true, "Ready to promote to Assist: the engine will propose actions for a human to approve.");
    }
}

/// <summary>
/// Pure blast-radius math and phrasing. "Verifying this mapping lets the engine
/// pause N campaign(s) currently spending $X/day for this client." — the
/// design-doc control, not a form field.
/// </summary>
public static class BlastRadius
{
    public static BlastRadiusSummary Compute(IReadOnlyList<CampaignFact> campaigns, string currency)
    {
        var live = campaigns.Where(c => c.IsLive).ToList();
        var spend = live.Sum(c => c.RecentDailySpend);
        return new BlastRadiusSummary(live.Count, spend, currency);
    }

    public static string Describe(BlastRadiusSummary summary)
    {
        if (summary.LiveCampaignCount == 0)
            return "No live campaigns for this client yet — verifying won't pause anything today, but it arms enforcement the moment ads go live.";

        var campaigns = summary.LiveCampaignCount == 1 ? "1 campaign" : $"{summary.LiveCampaignCount} campaigns";
        var money = Human.MoneyKpi(summary.DailySpend, summary.Currency);
        return $"Verifying this mapping lets the engine pause {campaigns} currently spending {money}/day for this client.";
    }
}

/// <summary>
/// Pure fuzzy suggester: ranks candidate external ids by how closely their label
/// matches the client's business name (campaign names usually carry it), with
/// sync recency as the tiebreaker. Best-effort — every result is a suggestion,
/// never an assertion.
/// </summary>
public static class LinkSuggester
{
    private static readonly HashSet<string> Stopwords =
        new(StringComparer.OrdinalIgnoreCase) { "the", "and", "llc", "inc", "ltd", "co", "corp", "of", "a" };

    public static IReadOnlyList<LinkSuggestion> Rank(string clientName, IEnumerable<SuggestionCandidate> candidates, int take = 6)
        => candidates
            .Select(c => (candidate: c, score: Similarity(clientName, c.Label)))
            .OrderByDescending(x => x.score)
            .ThenByDescending(x => x.candidate.SyncedAt)
            .Take(take)
            .Select(x => new LinkSuggestion(
                x.candidate.ExternalId,
                x.candidate.Label,
                x.candidate.Detail,
                x.score,
                Rationale(x.score)))
            .ToList();

    private static string Rationale(double score) => score switch
    {
        >= 0.6 => "Strong name match",
        >= 0.25 => "Partial name match",
        _ => "Unclaimed candidate — no name match, verify before linking",
    };

    /// <summary>0..1 similarity: token overlap (Jaccard) plus a containment bonus for name-in-label.</summary>
    public static double Similarity(string a, string b)
    {
        var tokensA = Tokenize(a);
        var tokensB = Tokenize(b);
        if (tokensA.Count == 0 || tokensB.Count == 0) return 0d;

        var intersection = tokensA.Intersect(tokensB).Count();
        var union = tokensA.Union(tokensB).Count();
        var jaccard = union == 0 ? 0d : (double)intersection / union;

        var normA = Normalize(a);
        var normB = Normalize(b);
        var containment = normA.Length > 0 && normB.Length > 0 && (normB.Contains(normA) || normA.Contains(normB))
            ? 0.3d
            : 0d;

        return Math.Min(1d, jaccard + containment);
    }

    private static HashSet<string> Tokenize(string value)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(value)) return tokens;
        foreach (var raw in value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var token = new string(raw.Where(char.IsLetterOrDigit).ToArray());
            if (token.Length < 2 || Stopwords.Contains(token)) continue;
            tokens.Add(token);
        }
        return tokens;
    }

    private static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
            if (char.IsLetterOrDigit(ch))
                sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }
}
