using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RD.Domain;
using RD.Infrastructure.Enforcement;
using RD.Infrastructure.Gateways;

namespace RD.Infrastructure.Sync;

public static class SyncServiceCollectionExtensions
{
    /// <summary>
    /// Registers Lane A: vendor gateway options, typed HttpClients, and the
    /// three sync jobs. The host is expected to have registered
    /// IDbContextFactory&lt;RdDbContext&gt; already (deliberately NOT added here) and
    /// to schedule the jobs' RunAsync via Hangfire recurring jobs after merge.
    /// </summary>
    public static IServiceCollection AddRdSync(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<StripeOptions>(config.GetSection(StripeOptions.SectionName));
        services.Configure<MetaOptions>(config.GetSection(MetaOptions.SectionName));
        services.Configure<GhlOptions>(config.GetSection(GhlOptions.SectionName));
        services.Configure<SafetyOptions>(config.GetSection(SafetyOptions.SectionName));
        services.Configure<EnforcementOptions>(config.GetSection(EnforcementOptions.SectionName));

        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<RetryHelper>();

        // Typed clients: each gateway configures its own BaseAddress + auth
        // headers from options in its constructor, so tests can construct them
        // directly against WireMock with a plain HttpClient.
        services.AddHttpClient<IStripeGateway, StripeGateway>();
        services.AddHttpClient<IMetaAdsGateway, MetaAdsGateway>();
        services.AddHttpClient<IGhlGateway, GhlGateway>();

        services.TryAddScoped<ClientStateBuilder>();
        services.TryAddScoped<ActionStager>();
        services.TryAddScoped<OutboxDispatcher>();
        services.TryAddScoped<ApprovalService>();
        services.TryAddScoped<KillSwitchService>();
        services.TryAddScoped<PromotionService>();

        services.AddScoped<StripeSyncJob>();
        services.AddScoped<MetaSyncJob>();
        services.AddScoped<GhlMessageSyncJob>();
        services.AddScoped<PolicyEvaluationJob>();
        services.AddScoped<GhlDeliveryVerificationJob>();
        services.AddScoped<ConvertExpirySweepJob>();

        return services;
    }
}
