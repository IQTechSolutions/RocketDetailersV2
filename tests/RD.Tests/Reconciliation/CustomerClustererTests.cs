using FluentAssertions;
using RD.Domain;
using RD.Infrastructure.Gateways;
using RD.Infrastructure.Reconciliation;

namespace RD.Tests.Reconciliation;

/// <summary>
/// The clustering that turns messy live Stripe into one-candidate-per-business.
/// The anchor case is a real one from the owner's account: "Maninder Singh" is
/// five separate Stripe customers across three emails (one with none) — only the
/// normalized name ties them together, and they must collapse to a single cluster.
/// </summary>
public class CustomerClustererTests
{
    private static StripeCustomerDto Cust(string id, string? name, string? email, string? country = "CA")
        => new(id, name, email, "USD", country, Delinquent: false, Created: DateTimeOffset.UnixEpoch);

    // ---------------- NameNormalizer ----------------

    [Theory]
    [InlineData("Maninder Singh", "maninder singh")]
    [InlineData("Maninder singh", "maninder singh")]   // case-only variant (row 4 in the real data)
    [InlineData("Bob's Detailing, LLC", "bobs detailing")]
    [InlineData("  The  Elite   Co ", "elite")]
    [InlineData(null, "")]
    public void Normalize_folds_case_punctuation_and_suffixes(string? raw, string expected)
        => NameNormalizer.Normalize(raw).Should().Be(expected);

    [Fact]
    public void Case_only_name_variants_normalize_equal()
        => NameNormalizer.Normalize("Maninder Singh").Should().Be(NameNormalizer.Normalize("Maninder singh"));

    // ---------------- CustomerClusterer ----------------

    [Fact]
    public void Five_Maninder_records_across_three_emails_collapse_to_one_cluster()
    {
        var customers = new[]
        {
            Cust("cus_1", "Maninder Singh", "mnisadhpur2111@gmail.com"),
            Cust("cus_2", "Maninder Singh", "mnisadhpur2111@gmail.com"), // shares email with cus_1
            Cust("cus_3", "Maninder Singh", "singh121.maninder@icloud.com"),
            Cust("cus_4", "Maninder singh", "carcaredetailing604@gmail.com"), // lowercase 's'
            Cust("cus_5", "Maninder Singh", null),                             // no email at all
        };

        var clusters = CustomerClusterer.Cluster(customers);

        clusters.Should().HaveCount(1);
        clusters[0].Members.Should().HaveCount(5);
        // A shared email exists inside the cluster (cus_1/cus_2), so confidence is High.
        clusters[0].Confidence.Should().Be(ClusterConfidence.High);
        clusters[0].Signals.Should().Contain(s => s.Contains("email"));
        clusters[0].Signals.Should().Contain(s => s.Contains("maninder singh"));
    }

    [Fact]
    public void Different_names_are_bridged_when_they_share_an_email()
    {
        var customers = new[]
        {
            Cust("cus_a", "Maninder Singh", "shared@x.com"),
            Cust("cus_b", "Car Care Detailing 604", "shared@x.com"), // different name, same email
        };

        var clusters = CustomerClusterer.Cluster(customers);

        clusters.Should().HaveCount(1);
        clusters[0].Confidence.Should().Be(ClusterConfidence.High);
    }

    [Fact]
    public void Same_generic_name_no_shared_email_still_clusters_but_at_lower_confidence()
    {
        // The over-cluster risk: two different businesses share a generic name.
        // We DO group them (to surface for a human) but flag it as Medium, not High.
        var customers = new[]
        {
            Cust("cus_x", "Elite Mobile Detailing", "owner1@a.com"),
            Cust("cus_y", "Elite Mobile Detailing", "owner2@b.com"),
        };

        var clusters = CustomerClusterer.Cluster(customers);

        clusters.Should().HaveCount(1);
        clusters[0].Members.Should().HaveCount(2);
        clusters[0].Confidence.Should().Be(ClusterConfidence.Medium); // same country, no shared email
    }

    [Fact]
    public void Genuinely_distinct_businesses_stay_separate()
    {
        var customers = new[]
        {
            Cust("cus_p", "Precision Mobile Detailing", "p@a.com"),
            Cust("cus_q", "Sparkle Auto Spa", "q@b.com"),
        };

        var clusters = CustomerClusterer.Cluster(customers);

        clusters.Should().HaveCount(2);
        clusters.Should().OnlyContain(c => c.Confidence == ClusterConfidence.Single);
    }
}
