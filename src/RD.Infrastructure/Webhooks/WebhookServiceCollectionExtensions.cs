using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RD.Domain;

namespace RD.Infrastructure.Webhooks;

public static class WebhookServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Stripe webhook receiver: options (Stripe:Webhook), the pure
    /// signature verifier, and the recoverable inbox ingestor. The host is expected
    /// to have registered IDbContextFactory&lt;RdDbContext&gt; already (the ingestor
    /// resolves its own contexts through it) and to call
    /// <c>app.MapStripeWebhook()</c> during endpoint routing.
    /// </summary>
    public static IServiceCollection AddStripeWebhooks(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<StripeWebhookOptions>(config.GetSection(StripeWebhookOptions.SectionName));

        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<StripeSignatureVerifier>();
        services.TryAddScoped<StripeWebhookIngestor>();

        return services;
    }
}
