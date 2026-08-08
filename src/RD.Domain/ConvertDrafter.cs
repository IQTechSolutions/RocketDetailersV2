namespace RD.Domain;

/// <summary>
/// Everything the drafter needs to decide what Stripe action a conversion WOULD take,
/// snapshotted from SQL so the decision is a pure function (replayable, unit-testable —
/// same pattern as EligibilityPolicy).
/// </summary>
/// <param name="AccountType">Own = flat service fee (automatable); Master = variable ad-spend billing (not automated yet).</param>
/// <param name="CurrencyCode">The client's billing currency. v1 automation is USD-only.</param>
/// <param name="HasPackage">Whether a package was chosen for this conversion.</param>
/// <param name="EffectiveStripePriceId">The Stripe Price on the package's current effective version, or null if unset.</param>
/// <param name="StripeCustomerId">The preferred customer, the only linked customer, or null when a new customer is needed.</param>
/// <param name="HasAmbiguousStripeCustomers">More than one customer is linked but no valid preference has been chosen.</param>
/// <param name="HasOpenStripeOwnershipInvestigation">The multi-customer cluster has not yet been confirmed as one business.</param>
/// <param name="HasExistingNonTerminalStripeSubscription">A linked customer already has a subscription that should be updated, not duplicated.</param>
/// <param name="HasSubscriptionWithoutCustomerLink">A linked subscription cannot be safely assigned to an active customer mapping.</param>
/// <param name="StripeEvidenceIsFresh">Whether a complete, recent Stripe sweep backs the subscription check.</param>
public readonly record struct ConvertDraftInput(
    AccountType AccountType,
    string CurrencyCode,
    bool HasPackage,
    string? EffectiveStripePriceId,
    string? StripeCustomerId,
    bool HasAmbiguousStripeCustomers = false,
    bool HasOpenStripeOwnershipInvestigation = false,
    bool HasExistingNonTerminalStripeSubscription = false,
    bool HasSubscriptionWithoutCustomerLink = false,
    bool StripeEvidenceIsFresh = true);

/// <summary>
/// The computed draft: what the app WOULD do at conversion, plus any blockers that make it
/// not auto-draftable. Purely advisory in Shadow — a human still performs every real write.
/// </summary>
public sealed record ConvertDraft(
    bool Ready,
    string Summary,
    AccountType AccountType,
    string? StripePriceId,
    string? StripeCustomerId,
    bool WouldCreateCustomer,
    string CurrencyCode,
    IReadOnlyList<string> Blockers);

/// <summary>
/// Pure draft logic for the Convert→Bill→Close wedge (A1 / Shadow). Given a snapshot, computes
/// the Stripe action a conversion would take — WITHOUT touching Stripe. Own-account converts to a
/// flat service-fee subscription on the package's Stripe price; master-account is deferred; missing
/// price, missing package, or non-USD surface as blockers so the operator sees exactly why a
/// conversion can't be auto-drafted yet.
/// </summary>
public static class ConvertDrafter
{
    public static ConvertDraft Draft(ConvertDraftInput s)
    {
        var blockers = new List<string>();

        // Master-account billing covers variable ad spend — a fixed Stripe price can't represent it.
        // Deferred to a follow-on wedge; the branch stays here so master is a drop-in later.
        if (s.AccountType == AccountType.Master)
            blockers.Add("Master-account billing covers variable ad spend and isn't automated yet — handle it manually.");

        // v1 automation is USD-only (matches EligibilityPolicy's currency guard).
        if (!string.Equals(s.CurrencyCode, "USD", StringComparison.OrdinalIgnoreCase))
            blockers.Add($"Client bills in {s.CurrencyCode}; automated conversion is USD-only.");

        if (!s.HasPackage)
            blockers.Add("No package selected — pick one so the draft has a price to bill.");
        else if (string.IsNullOrWhiteSpace(s.EffectiveStripePriceId))
            blockers.Add("The selected package has no Stripe price set — set one in Packages.");

        if (s.HasAmbiguousStripeCustomers)
            blockers.Add("More than one Stripe customer is linked — choose the preferred billing customer on Client details first.");
        if (s.HasOpenStripeOwnershipInvestigation)
            blockers.Add("Stripe customer ownership is still unconfirmed — resolve the multiple-customer investigation before billing.");
        if (s.HasExistingNonTerminalStripeSubscription)
            blockers.Add("A linked Stripe customer already has a current subscription — review or update it instead of creating a second subscription.");
        if (s.HasSubscriptionWithoutCustomerLink)
            blockers.Add("A linked Stripe subscription has no matching active customer link — correct the mapping before billing.");
        if (!s.StripeEvidenceIsFresh)
            blockers.Add("Stripe subscription data is not fresh — complete a Stripe sync before billing.");

        var wouldCreateCustomer = string.IsNullOrWhiteSpace(s.StripeCustomerId) && !s.HasAmbiguousStripeCustomers;
        var customerId = string.IsNullOrWhiteSpace(s.StripeCustomerId) ? null : s.StripeCustomerId;
        var ready = blockers.Count == 0;

        var customerPhrase = wouldCreateCustomer ? "a new Stripe customer" : $"customer {customerId}";
        var summary = ready
            ? $"Would create a subscription on {s.EffectiveStripePriceId} for {customerPhrase} ({s.CurrencyCode})."
            : $"Not auto-draftable: {string.Join(" ", blockers)}";

        return new ConvertDraft(
            Ready: ready,
            Summary: summary,
            AccountType: s.AccountType,
            StripePriceId: ready ? s.EffectiveStripePriceId : null,
            StripeCustomerId: customerId,
            WouldCreateCustomer: wouldCreateCustomer,
            CurrencyCode: s.CurrencyCode,
            Blockers: blockers);
    }
}
