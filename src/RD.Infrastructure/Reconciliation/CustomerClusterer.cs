using RD.Domain;
using RD.Infrastructure.Gateways;

namespace RD.Infrastructure.Reconciliation;

/// <summary>Confidence that a multi-member cluster really is one business.</summary>
public enum ClusterConfidence { Single, High, Medium, Low }

/// <summary>
/// A set of Stripe customer records believed to be one business, with the
/// human-readable signals that grouped them. NEVER treated as a decided merge —
/// a multi-member cluster requires same-business confirmation; separate-business
/// cases stay open for manual mapping correction outside this workflow.
/// </summary>
public sealed record CustomerCluster(
    IReadOnlyList<StripeCustomerDto> Members,
    string DisplayName,
    ClusterConfidence Confidence,
    IReadOnlyList<string> Signals);

/// <summary>
/// Pure clustering of Stripe customers into candidate businesses. Two records are
/// linked when they share the <see cref="NameNormalizer"/>-normalized name OR the
/// same email; clusters are the transitive closure (union-find) of those links.
/// This is why one business scattered across 5 customer records and 3 emails
/// still collapses to a single candidate — the shared name bridges the email gap.
/// </summary>
public static class CustomerClusterer
{
    public static IReadOnlyList<CustomerCluster> Cluster(IReadOnlyList<StripeCustomerDto> customers)
    {
        var n = customers.Count;
        var parent = new int[n];
        for (var i = 0; i < n; i++) parent[i] = i;

        int Find(int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }
        void Union(int a, int b) => parent[Find(a)] = Find(b);

        // Edge 1: identical normalized name.
        var firstByName = new Dictionary<string, int>();
        for (var i = 0; i < n; i++)
        {
            var key = NameNormalizer.Normalize(customers[i].Name);
            if (key.Length == 0) continue;
            if (firstByName.TryGetValue(key, out var j)) Union(i, j);
            else firstByName[key] = i;
        }

        // Edge 2: identical email (strong same-person signal, even across name variants).
        var firstByEmail = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < n; i++)
        {
            var email = customers[i].Email;
            if (string.IsNullOrWhiteSpace(email)) continue;
            if (firstByEmail.TryGetValue(email, out var j)) Union(i, j);
            else firstByEmail[email] = i;
        }

        var components = new Dictionary<int, List<StripeCustomerDto>>();
        for (var i = 0; i < n; i++)
        {
            var root = Find(i);
            if (!components.TryGetValue(root, out var list)) components[root] = list = [];
            list.Add(customers[i]);
        }

        return components.Values.Select(Build).ToList();
    }

    private static CustomerCluster Build(List<StripeCustomerDto> m)
    {
        var display = m.Select(x => x.Name).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))
                      ?? m.Select(x => x.Email).FirstOrDefault(x => x is not null)
                      ?? m[0].Id;

        if (m.Count == 1) return new CustomerCluster(m, display!, ClusterConfidence.Single, []);

        var sharedEmail = m.Where(x => x.Email is not null)
            .GroupBy(x => x.Email!, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1);
        var normNames = m.Select(x => NameNormalizer.Normalize(x.Name)).Where(s => s.Length > 0).Distinct().ToList();
        var namedMembers = m.Count(x => !string.IsNullOrWhiteSpace(x.Name));
        var countries = m.Where(x => x.Country is not null).Select(x => x.Country!).Distinct().ToList();
        var sameCountry = countries.Count <= 1;

        var signals = new List<string>();
        if (sharedEmail) signals.Add("shares an email");
        if (normNames.Count == 1 && namedMembers > 1) signals.Add($"same normalized name '{normNames[0]}'");
        else if (normNames.Count > 1) signals.Add($"linked by email across name variants: {string.Join(" / ", normNames)}");
        if (countries.Count == 1 && m.Count(x => x.Country is not null) > 1) signals.Add($"all {countries[0]}");

        var confidence = sharedEmail ? ClusterConfidence.High
                       : sameCountry ? ClusterConfidence.Medium
                       : ClusterConfidence.Low;

        return new CustomerCluster(m, display!, confidence, signals);
    }
}
