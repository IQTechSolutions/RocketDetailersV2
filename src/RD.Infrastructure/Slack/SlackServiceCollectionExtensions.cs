using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RD.Domain;
using RD.Infrastructure.Enforcement;

namespace RD.Infrastructure.Slack;

public static class SlackServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Slack command center: options (Slack), the pure signature
    /// verifier, the incoming-webhook notifier, the notify-once recurring job, and
    /// the Slack-user → Operator authorizer. The host is expected to have registered
    /// IDbContextFactory&lt;RdDbContext&gt; and Identity already, to call
    /// <c>app.MapSlackInteractivity()</c> during endpoint routing, to schedule
    /// <c>SlackNotificationJob.RunAsync</c> as a recurring job, and to call
    /// <c>SlackUserSeeder.SeedAsync</c> after the Identity seeder.
    /// </summary>
    public static IServiceCollection AddSlack(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<SlackOptions>(config.GetSection(SlackOptions.SectionName));

        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<SlackSignatureVerifier>();
        services.TryAddScoped<SlackAuthorizer>();
        // ApprovalService is also registered by AddRdSync; TryAdd keeps Slack self-contained.
        services.TryAddScoped<ApprovalService>();

        // Raw-HttpClient notifier (posts Block Kit to the incoming webhook).
        services.AddHttpClient<SlackNotifier>();
        services.AddScoped<SlackNotificationJob>();

        return services;
    }
}
