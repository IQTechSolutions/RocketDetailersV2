namespace RD.Infrastructure.Gateways;

// Config-key contract (host supplies values; NOTHING here is ever committed):
//   Stripe:ApiKey, Stripe:BaseUrl
//   Meta:AccessToken, Meta:AdAccountId, Meta:BaseUrl, Meta:AccountCurrency
//   Ghl:BaseUrl, Ghl:Locations:N:LocationId, Ghl:Locations:N:Token
// Secrets (ApiKey/AccessToken/Token) are read from configuration only and are
// never logged, never placed in URLs (always Authorization headers).

public sealed class StripeOptions
{
    public const string SectionName = "Stripe";

    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "https://api.stripe.com";
    /// <summary>Optional Stripe-Version pin. Empty = account default version. Set after M0 confirms the account's version.</summary>
    public string ApiVersion { get; set; } = "";
}

public sealed class MetaOptions
{
    public const string SectionName = "Meta";

    public string AccessToken { get; set; } = "";
    /// <summary>The master ad account, e.g. "act_1234567890" (bare digits also accepted).</summary>
    public string AdAccountId { get; set; } = "";
    public string BaseUrl { get; set; } = "https://graph.facebook.com/v23.0";
    /// <summary>Insights spend arrives in the ad account's currency with no per-row currency field.</summary>
    public string AccountCurrency { get; set; } = "USD";
    /// <summary>How many days back the daily-insights sweep covers (1 = yesterday+today). Keep small for the recurring job; raise for a one-off backfill.</summary>
    public int InsightsLookbackDays { get; set; } = 1;
}

public sealed class GhlLocationOptions
{
    public string LocationId { get; set; } = "";
    /// <summary>Per-location Private Integration Token. Exactly 2 locations exist in production.</summary>
    public string Token { get; set; } = "";
}

public sealed class GhlOptions
{
    public const string SectionName = "Ghl";

    public string BaseUrl { get; set; } = "https://services.leadconnectorhq.com";
    public List<GhlLocationOptions> Locations { get; set; } = [];
    /// <summary>How many most-recent conversations each sweep visits per location.</summary>
    public int ConversationSweepLimit { get; set; } = 100;
}

/// <summary>
/// The staging safety profile (design doc F5 + outside-voice continuity).
/// SAFE BY DEFAULT: with no config, the app cannot touch a real client's ads
/// or phone — GHL sends redirect to a test contact, and Meta writes are
/// refused unless the target is the dedicated canary campaign. Production
/// enforcement is a deliberate ops flip AFTER the M0 human gates are met, not
/// an accident of a missing setting.
/// </summary>
public sealed class SafetyOptions
{
    public const string SectionName = "Safety";

    /// <summary>When true (default), ALL GHL sends are redirected to the test contact regardless of the intended recipient.</summary>
    public bool GhlTestMode { get; set; } = true;
    public string TestContactLocationId { get; set; } = "";
    public string TestContactId { get; set; } = "";

    /// <summary>When false (default), Meta pause/resume is permitted ONLY against <see cref="CanaryCampaignId"/>; any other target throws.</summary>
    public bool AllowProductionMetaWrites { get; set; } = false;
    /// <summary>The dedicated paused canary campaign (owner creates it; the old "$1/day" id turned out to be a real client's ads).</summary>
    public string CanaryCampaignId { get; set; } = "";
}
