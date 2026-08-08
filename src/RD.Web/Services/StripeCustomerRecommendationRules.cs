namespace RD.Web.Services;

/// <summary>
/// Pure, conservative selection of a Stripe customer for a new subscription.
/// The caller supplies only successful subscription-invoice timestamps; invoice
/// status and subscription ownership must be verified before building the input.
/// </summary>
public static class StripeCustomerRecommendationRules
{
    public static readonly TimeSpan DefaultSyncFreshnessBound = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan DefaultPaidInvoiceLookback = TimeSpan.FromDays(30);

    private static readonly HashSet<string> PositiveSubscriptionStatuses =
        new(StringComparer.OrdinalIgnoreCase) { "active", "trialing" };

    private static readonly HashSet<string> TerminalSubscriptionStatuses =
        new(StringComparer.OrdinalIgnoreCase) { "canceled", "incomplete_expired" };

    public static StripeCustomerRecommendation Recommend(
        StripeCustomerRecommendationInput input,
        DateTimeOffset now)
        => Recommend(input, now, DefaultSyncFreshnessBound, DefaultPaidInvoiceLookback);

    public static StripeCustomerRecommendation Recommend(
        StripeCustomerRecommendationInput input,
        DateTimeOffset now,
        TimeSpan freshnessBound,
        TimeSpan paidInvoiceLookback)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (freshnessBound < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(freshnessBound));
        if (paidInvoiceLookback < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(paidInvoiceLookback));

        if (input.CompletedStripeSyncAt is not { } completedAt)
            return Abstain(StripeCustomerRecommendationReason.NoCompletedStripeSync);

        if (completedAt > now)
            return Abstain(StripeCustomerRecommendationReason.FutureCompletedStripeSync);

        if (now - completedAt > freshnessBound)
            return Abstain(StripeCustomerRecommendationReason.StaleCompletedStripeSync);

        // Merge repeated rows for the same Stripe customer before deriving owner
        // sets. This makes the result independent of query shape and input order.
        var evidenceByCustomer = input.Candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.ExternalId))
            .GroupBy(candidate => candidate.ExternalId, StringComparer.Ordinal)
            .Select(group => new
            {
                ExternalId = group.Key,
                Statuses = group
                    .SelectMany(candidate => candidate.SubscriptionStatuses)
                    .Select(NormalizeStatus)
                    .Where(status => status.Length > 0)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase),
                PaidAt = group
                    .SelectMany(candidate => candidate.PaidSubscriptionInvoiceAt)
                    .ToHashSet(),
            })
            .OrderBy(candidate => candidate.ExternalId, StringComparer.Ordinal)
            .ToList();

        var billingOwners = evidenceByCustomer
            .Where(candidate => candidate.Statuses.Any(IsNonTerminal))
            .Select(candidate => candidate.ExternalId)
            .ToList();

        if (billingOwners.Count > 1)
            return Abstain(StripeCustomerRecommendationReason.MultipleBillingOwners);

        var positiveOwners = evidenceByCustomer
            .Where(candidate => candidate.Statuses.Any(IsPositive))
            .Select(candidate => candidate.ExternalId)
            .ToList();

        var recentPaidOwners = evidenceByCustomer
            .Where(candidate => candidate.PaidAt.Any(paidAt =>
                paidAt <= now && now - paidAt <= paidInvoiceLookback))
            .Select(candidate => candidate.ExternalId)
            .ToList();

        if (recentPaidOwners.Count > 1)
            return Abstain(StripeCustomerRecommendationReason.MultipleRecentPaidOwners);

        // Multiple positive owners are necessarily multiple non-terminal billing
        // owners and therefore returned above. Keep this guard explicit so the
        // safety property remains true if the status sets evolve independently.
        if (positiveOwners.Count > 1)
            return Abstain(StripeCustomerRecommendationReason.MultipleBillingOwners);

        var billingOwner = billingOwners.SingleOrDefault();
        var positiveOwner = positiveOwners.SingleOrDefault();
        var recentPaidOwner = recentPaidOwners.SingleOrDefault();

        if (billingOwner is not null &&
            recentPaidOwner is not null &&
            !StringComparer.Ordinal.Equals(billingOwner, recentPaidOwner))
        {
            return Abstain(StripeCustomerRecommendationReason.ConflictingSubscriptionAndPaidOwners);
        }

        if (positiveOwner is not null)
        {
            return new StripeCustomerRecommendation(
                positiveOwner,
                StripeCustomerRecommendationReason.ActiveOrTrialingSubscription);
        }

        if (recentPaidOwner is not null)
        {
            return new StripeCustomerRecommendation(
                recentPaidOwner,
                StripeCustomerRecommendationReason.RecentPaidSubscriptionInvoice);
        }

        return Abstain(StripeCustomerRecommendationReason.NoPositiveEvidence);
    }

    private static string NormalizeStatus(string? status)
        => status?.Trim() ?? string.Empty;

    private static bool IsPositive(string status)
        => PositiveSubscriptionStatuses.Contains(status);

    private static bool IsNonTerminal(string status)
        => !TerminalSubscriptionStatuses.Contains(status);

    private static StripeCustomerRecommendation Abstain(StripeCustomerRecommendationReason reason)
        => new(null, reason);
}

public sealed record StripeCustomerRecommendationInput(
    DateTimeOffset? CompletedStripeSyncAt,
    IReadOnlyList<StripeCustomerRecommendationEvidence> Candidates);

public sealed record StripeCustomerRecommendationEvidence(
    string ExternalId,
    IReadOnlyList<string> SubscriptionStatuses,
    IReadOnlyList<DateTimeOffset> PaidSubscriptionInvoiceAt);

public sealed record StripeCustomerRecommendation(
    string? RecommendedExternalId,
    StripeCustomerRecommendationReason Reason)
{
    public bool IsRecommendation => RecommendedExternalId is not null;
}

public enum StripeCustomerRecommendationReason
{
    NoCompletedStripeSync = 0,
    FutureCompletedStripeSync = 1,
    StaleCompletedStripeSync = 2,
    MultipleBillingOwners = 3,
    MultipleRecentPaidOwners = 4,
    ConflictingSubscriptionAndPaidOwners = 5,
    NoPositiveEvidence = 6,
    ActiveOrTrialingSubscription = 7,
    RecentPaidSubscriptionInvoice = 8,
}
