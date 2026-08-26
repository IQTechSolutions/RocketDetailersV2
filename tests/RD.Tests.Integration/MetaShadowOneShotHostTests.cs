using System.Diagnostics;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using RD.Domain;
using RD.Domain.Entities;
using RD.Domain.Policy;
using RD.Infrastructure;
using RD.Web.Services;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace RD.Tests.Integration;

public sealed class MetaShadowOneShotHostTests
{
    [Fact]
    public async Task One_shot_host_uses_only_Meta_GET_writes_only_V2_audit_and_never_starts_Hangfire()
    {
        using var meta = WireMockServer.Start();
        var databaseName = "RocketDetailers_OneShot_" + Guid.NewGuid().ToString("N");
        var server = Environment.GetEnvironmentVariable("RD_TEST_SQL") is { Length: > 0 } configured
            ? configured
            : @"(localdb)\MSSQLLocalDB";
        var connectionString =
            $@"Server={server};Database={databaseName};Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True";
        var options = new DbContextOptionsBuilder<RdDbContext>()
            .UseSqlServer(connectionString)
            .AddInterceptors(new AppendOnlyInterceptor())
            .Options;
        var now = DateTimeOffset.UtcNow;
        var clientId = Guid.NewGuid();
        var decisionId = Guid.NewGuid();
        var predictionId = Guid.NewGuid();
        const string campaignId = "host_compare_campaign";

        try
        {
            await using (var seed = new RdDbContext(options))
            {
                await seed.Database.MigrateAsync();
                seed.Clients.Add(new Client
                {
                    Id = clientId,
                    BusinessName = "One-shot host test",
                    ContractType = ContractType.Paid,
                    AccountType = AccountType.Master,
                    EnforcementMode = EnforcementMode.Shadow,
                    CreatedAt = now.AddDays(-2),
                });
                seed.IdentityLinks.Add(new IdentityLink
                {
                    Id = Guid.NewGuid(),
                    ClientId = clientId,
                    System = ExternalSystem.Meta,
                    Kind = LinkKind.Campaign,
                    ExternalId = campaignId,
                    CreatedAt = now.AddDays(-2),
                });
                seed.Decisions.Add(new Decision
                {
                    Id = decisionId,
                    ClientId = clientId,
                    EvaluatedAt = now.AddHours(-1),
                    PolicyVersion = "host-test",
                    StateSnapshotJson = "{}",
                    ProposedAction = ProposedActionType.Pause,
                    Mode = EnforcementMode.Shadow,
                    TargetCampaignIdsJson = $"[\"{campaignId}\"]",
                    Reason = "Host test prediction",
                });
                seed.MetaShadowPredictions.Add(new MetaShadowPrediction
                {
                    Id = predictionId,
                    ClientId = clientId,
                    DecisionId = decisionId,
                    CampaignId = campaignId,
                    ProposedAction = ProposedActionType.Pause,
                    DesiredStatus = MetaShadowComparison.PausedStatus,
                    TargetState = MetaShadowTargetState.Executable,
                    StartedAt = now.AddHours(-1),
                });
                await seed.SaveChangesAsync();
            }

            meta.Given(Request.Create()
                    .WithPath("/act_1234/activities")
                    .UsingGet())
                .RespondWith(Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody($$"""
                        {
                          "data": [
                            {
                              "event_time": {{now.AddMinutes(-30).ToUnixTimeSeconds()}},
                              "event_type": "update_campaign_run_status",
                              "object_id": "{{campaignId}}",
                              "extra_data": { "old_value": "Active", "new_value": "Inactive" }
                            }
                          ]
                        }
                        """));

            var webAssembly = typeof(MetaShadowOneShotMode).Assembly.Location;
            var start = new ProcessStartInfo
            {
                FileName = "dotnet",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add(webAssembly);
            start.ArgumentList.Add(MetaShadowOneShotMode.Switch);
            start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
            start.Environment["DOTNET_ENVIRONMENT"] = "Production";
            start.Environment["ConnectionStrings__RocketDetailers"] = connectionString;
            start.Environment["Meta__AccessToken"] = "host_test_token";
            start.Environment["Meta__AdAccountId"] = "act_1234";
            start.Environment["Meta__BaseUrl"] = meta.Url!;
            start.Environment["Safety__AllowProductionMetaWrites"] = "false";
            start.Environment["Safety__GhlTestMode"] = "true";
            start.Environment["Logging__LogLevel__Default"] = "Warning";

            using var process = Process.Start(start)!;
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            process.ExitCode.Should().Be(0, stderr);
            stdout.Should().Contain("\"Matched\": 1");
            stdout.Should().NotContain(clientId.ToString());
            stdout.Should().NotContain(campaignId);
            meta.LogEntries.Should().ContainSingle();
            var request = meta.LogEntries.Single().RequestMessage;
            request.Should().NotBeNull();
            request!.Method.Should().Be("GET");
            request.Headers!["Authorization"]
                .Should().Contain("Bearer host_test_token");

            await using var assert = new RdDbContext(options);
            (await assert.MetaActivityFacts.CountAsync()).Should().Be(1);
            (await assert.OutboxActions.CountAsync()).Should().Be(0);
            var hangfireSchemaExists = await assert.Database
                .SqlQueryRaw<int>("SELECT CASE WHEN SCHEMA_ID('hangfire') IS NULL THEN 0 ELSE 1 END AS [Value]")
                .SingleAsync();
            hangfireSchemaExists.Should().Be(0);
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await using var cleanup = new RdDbContext(options);
            await cleanup.Database.EnsureDeletedAsync();
        }
    }
}
