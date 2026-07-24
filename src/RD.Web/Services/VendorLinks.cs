using Microsoft.Extensions.Options;
using RD.Domain;
using RD.Infrastructure.Gateways;

namespace RD.Web.Services;

/// <summary>
/// Builds operator-facing deep-links into the vendor dashboards (Stripe, Meta
/// Ads Manager, GoHighLevel, ClickUp) so an operator can double-check a linked
/// identity against the source of truth before trusting or acting on it.
///
/// Best-effort by design: <see cref="For"/> returns null when a reliable link
/// can't be built (missing config, or a system/kind with no dashboard route),
/// and the UI then just shows the raw id. Nothing here ever carries a secret —
/// URLs are built from ids and public account identifiers only.
/// </summary>
public sealed class VendorLinks
{
    private readonly string _metaAdAccount;      // bare digits, no act_ prefix
    private readonly bool _stripeTestMode;
    private readonly string? _singleGhlLocation; // fallback when exactly one location is configured

    public VendorLinks(IOptions<StripeOptions> stripe, IOptions<MetaOptions> meta, IOptions<GhlOptions> ghl, IConfiguration config)
    {
        // Stripe dashboard is /test/… on test keys and /… on live keys. An explicit
        // Stripe:DashboardMode override wins; otherwise derive it from the key prefix
        // so the correct link works out of the box with no extra config.
        var mode = config["Stripe:DashboardMode"];
        _stripeTestMode = mode?.Trim().ToLowerInvariant() switch
        {
            "test" => true,
            "live" => false,
            _ => StartsWithAny(stripe.Value.ApiKey, "sk_test_", "rk_test_"),
        };

        _metaAdAccount = DigitsOnly(meta.Value.AdAccountId);

        _singleGhlLocation = ghl.Value.Locations.Count == 1
            ? NullIfBlank(ghl.Value.Locations[0].LocationId)
            : null;
    }

    /// <summary>
    /// A deep-link to the vendor record for one identity link, or null when one
    /// can't be built. <paramref name="ghlLocationId"/> is required to link a GHL
    /// contact (its dashboard route is location-scoped); when omitted, a single
    /// configured location is used as a fallback.
    /// </summary>
    public string? For(ExternalSystem system, LinkKind kind, string externalId, string? ghlLocationId = null)
    {
        if (string.IsNullOrWhiteSpace(externalId)) return null;
        var id = externalId.Trim();

        return (system, kind) switch
        {
            (ExternalSystem.Stripe, LinkKind.Customer) => $"{StripeBase}customers/{id}",
            (ExternalSystem.Stripe, LinkKind.Subscription) => $"{StripeBase}subscriptions/{id}",
            (ExternalSystem.Meta, LinkKind.Campaign) => MetaCampaign(id),
            (ExternalSystem.Meta, LinkKind.AdAccount) => MetaAdAccount(id),
            (ExternalSystem.Ghl, LinkKind.Contact) => GhlContact(id, ghlLocationId ?? _singleGhlLocation),
            (ExternalSystem.ClickUp, LinkKind.Task) => $"https://app.clickup.com/t/{id}",
            _ => null,
        };
    }

    /// <summary>
    /// Best-effort deep-link from a system + external id when no <see cref="LinkKind"/>
    /// is known (e.g. an investigation row that stored only those). Stripe kind is
    /// inferred from the id prefix (sub_/cus_); other systems have one dominant kind.
    /// </summary>
    public string? ForSystem(ExternalSystem system, string externalId, string? ghlLocationId = null)
    {
        if (string.IsNullOrWhiteSpace(externalId)) return null;
        var id = externalId.Trim();

        LinkKind? kind = system switch
        {
            ExternalSystem.Stripe => id.StartsWith("sub_", StringComparison.OrdinalIgnoreCase) ? LinkKind.Subscription
                : id.StartsWith("cus_", StringComparison.OrdinalIgnoreCase) ? LinkKind.Customer
                : null,
            ExternalSystem.Meta => LinkKind.Campaign,
            ExternalSystem.Ghl => LinkKind.Contact,
            ExternalSystem.ClickUp => LinkKind.Task,
            _ => null,
        };
        return kind is null ? null : For(system, kind.Value, id, ghlLocationId);
    }

    /// <summary>Tooltip/aria label for the destination of a link, e.g. "Open in Stripe".</summary>
    public static string Label(ExternalSystem system) => system switch
    {
        ExternalSystem.Stripe => "Open in Stripe",
        ExternalSystem.Meta => "Open in Meta Ads Manager",
        ExternalSystem.Ghl => "Open in GoHighLevel",
        ExternalSystem.ClickUp => "Open in ClickUp",
        _ => "Open externally",
    };

    private string StripeBase => _stripeTestMode
        ? "https://dashboard.stripe.com/test/"
        : "https://dashboard.stripe.com/";

    private string? MetaCampaign(string campaignId)
        => string.IsNullOrEmpty(_metaAdAccount)
            ? null
            : $"https://adsmanager.facebook.com/adsmanager/manage/campaigns?act={_metaAdAccount}&selected_campaign_ids={campaignId}";

    private static string? MetaAdAccount(string adAccountId)
    {
        var digits = DigitsOnly(adAccountId);
        return string.IsNullOrEmpty(digits)
            ? null
            : $"https://adsmanager.facebook.com/adsmanager/manage/campaigns?act={digits}";
    }

    private static string? GhlContact(string contactId, string? locationId)
        => string.IsNullOrEmpty(locationId)
            ? null
            : $"https://app.gohighlevel.com/v2/location/{locationId}/contacts/detail/{contactId}";

    private static bool StartsWithAny(string? value, params string[] prefixes)
        => value is not null && prefixes.Any(p => value.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    private static string DigitsOnly(string? s) => new((s ?? "").Where(char.IsDigit).ToArray());

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
