namespace RD.Infrastructure.Webhooks;

/// <summary>
/// Config section "Stripe:Webhook" (host supplies values; the signing secret is
/// NEVER committed and NEVER logged — see StripeSignatureVerifier).
/// Keys: Stripe:Webhook:SigningSecret, Stripe:Webhook:ToleranceSeconds.
/// </summary>
public sealed class StripeWebhookOptions
{
    public const string SectionName = "Stripe:Webhook";

    /// <summary>The endpoint's `whsec_...` signing secret. Read from configuration only.</summary>
    public string SigningSecret { get; set; } = "";

    /// <summary>Replay-protection window: reject events whose timestamp skews further than this. Stripe's default is 300s.</summary>
    public int ToleranceSeconds { get; set; } = 300;
}
