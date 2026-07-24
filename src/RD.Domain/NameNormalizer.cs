using System.Text.RegularExpressions;

namespace RD.Domain;

/// <summary>
/// THE canonical name normalizer for cross-system reconciliation. Every system
/// and every sheet — Stripe (person) names, GHL, ClickUp, the business-name
/// sheets — MUST run names through this exact function, or the clusters they
/// produce will not line up and cross-source reconciliation silently fails.
///
/// It is deliberately loose: its only job is to SURFACE likely-same records for
/// a human to confirm, never to decide identity. It knowingly both over-clusters
/// (common names collide) and under-clusters (a Stripe person name will never
/// match a business-name sheet) — those exceptions are handled by
/// cluster-then-confirm, not by making this stricter.
/// </summary>
public static partial class NameNormalizer
{
    /// <summary>Lower-cases, strips punctuation, drops common company suffixes, collapses whitespace. Empty for null/blank.</summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        // Strip apostrophes FIRST with no space, so "Bob's" and "Bobs" both fold to
        // "bobs" (spacing them would split the exact variant we mean to catch).
        var s = Apostrophes().Replace(raw.ToLowerInvariant(), "");
        s = NonAlnum().Replace(s, " ");
        s = Suffixes().Replace(s, " ");
        return Spaces().Replace(s, " ").Trim();
    }

    [GeneratedRegex("['’`]")] private static partial Regex Apostrophes();
    [GeneratedRegex("[^a-z0-9 ]")] private static partial Regex NonAlnum();
    [GeneratedRegex(@"\b(llc|inc|ltd|co|corp|company|the)\b")] private static partial Regex Suffixes();
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
}
