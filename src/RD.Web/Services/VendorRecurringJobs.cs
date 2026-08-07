using Hangfire;
using RD.Infrastructure.Slack;
using RD.Infrastructure.Sync;

namespace RD.Web.Services;

/// <summary>
/// Reconciles SQL-backed Hangfire vendor schedules with the credentials loaded
/// for this process. Removing disabled schedules matters because Hangfire keeps
/// recurring jobs across application restarts.
/// </summary>
public static class VendorRecurringJobs
{
    public static void Reconcile(
        IRecurringJobManager recurring,
        IConfiguration configuration)
    {
        if (!string.IsNullOrWhiteSpace(configuration["Stripe:ApiKey"]))
            recurring.AddOrUpdate<StripeSyncJob>(
                "stripe-sync",
                job => job.RunAsync(CancellationToken.None),
                "*/15 * * * *");
        else
            recurring.RemoveIfExists("stripe-sync");

        if (!string.IsNullOrWhiteSpace(configuration["Meta:AccessToken"])
            && !string.IsNullOrWhiteSpace(configuration["Meta:AdAccountId"]))
            recurring.AddOrUpdate<MetaSyncJob>(
                "meta-sync",
                job => job.RunAsync(CancellationToken.None),
                "0 * * * *");
        else
            recurring.RemoveIfExists("meta-sync");

        if (!string.IsNullOrWhiteSpace(configuration["Ghl:Locations:0:Token"]))
            recurring.AddOrUpdate<GhlMessageSyncJob>(
                "ghl-message-sync",
                job => job.RunAsync(CancellationToken.None),
                "*/15 * * * *");
        else
            recurring.RemoveIfExists("ghl-message-sync");

        if (!string.IsNullOrWhiteSpace(configuration["Slack:IncomingWebhookUrl"]))
            recurring.AddOrUpdate<SlackNotificationJob>(
                "slack-notify",
                job => job.RunAsync(CancellationToken.None),
                "* * * * *");
        else
            recurring.RemoveIfExists("slack-notify");
    }
}
