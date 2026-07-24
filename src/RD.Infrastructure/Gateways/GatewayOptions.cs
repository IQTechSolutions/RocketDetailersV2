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
