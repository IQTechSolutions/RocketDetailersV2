using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Microsoft.Extensions.Configuration;
using RD.Web.Services;

namespace RD.Tests;

public sealed class RecurringVendorJobRegistrationTests
{
    [Fact]
    public void Reconcile_RemovesPersistedVendorJobs_WhenCredentialsAreMissing()
    {
        var configuration = new ConfigurationBuilder().Build();
        var recurring = new RecordingRecurringJobManager();

        VendorRecurringJobs.Reconcile(recurring, configuration);

        recurring.Removed.Should().BeEquivalentTo(
            "stripe-sync",
            "meta-sync",
            "ghl-message-sync",
            "slack-notify");
        recurring.Added.Should().BeEmpty();
    }

    [Fact]
    public void Reconcile_AddsVendorJobs_WhenCredentialsAreConfigured()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Stripe:ApiKey"] = "rk_test_dummy",
            ["Meta:AccessToken"] = "meta_test_dummy",
            ["Meta:AdAccountId"] = "act_1234",
            ["Ghl:Locations:0:Token"] = "pit-test-dummy",
            ["Slack:IncomingWebhookUrl"] = "https://hooks.slack.test/dummy"
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
        var recurring = new RecordingRecurringJobManager();

        VendorRecurringJobs.Reconcile(recurring, configuration);

        recurring.Added.Should().BeEquivalentTo(
            "stripe-sync",
            "meta-sync",
            "ghl-message-sync",
            "slack-notify");
        recurring.Removed.Should().BeEmpty();
    }

    [Fact]
    public void Reconcile_RemovesMetaJob_WhenAdAccountIdIsMissing()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Meta:AccessToken"] = "meta_test_dummy"
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
        var recurring = new RecordingRecurringJobManager();

        VendorRecurringJobs.Reconcile(recurring, configuration);

        recurring.Removed.Should().Contain("meta-sync");
        recurring.Added.Should().NotContain("meta-sync");
    }

    private sealed class RecordingRecurringJobManager : IRecurringJobManager
    {
        public HashSet<string> Added { get; } = [];
        public HashSet<string> Removed { get; } = [];

        public void AddOrUpdate(
            string recurringJobId,
            Job job,
            string cronExpression,
            RecurringJobOptions options) => Added.Add(recurringJobId);

        public void Trigger(string recurringJobId) =>
            throw new NotSupportedException("Trigger is not used by registration reconciliation.");

        public void RemoveIfExists(string recurringJobId) => Removed.Add(recurringJobId);
    }
}
