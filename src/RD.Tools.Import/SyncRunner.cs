using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RD.Infrastructure;
using RD.Infrastructure.Sync;

namespace RD.Tools.Import;

/// <summary>
/// One-shot sync + policy evaluation, for the deploy gate and manual runs:
///   dotnet run --project src/RD.Tools.Import -- sync [stripe] [meta] [ghl] [eval]
/// (no selector = all). Credentials come from environment variables
/// (Stripe__ApiKey, Meta__AccessToken, ...) — never from files in this repo.
/// </summary>
public static class SyncRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets(System.Reflection.Assembly.GetExecutingAssembly(), optional: true)
            .AddEnvironmentVariables()
            .Build();

        var conn = Environment.GetEnvironmentVariable("RD_CONN")
            ?? config.GetConnectionString("RocketDetailers")
            ?? "Server=(localdb)\\MSSQLLocalDB;Database=RocketDetailers;Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True";

        var services = new ServiceCollection();
        services.AddLogging(l => l.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Information));
        services.AddSingleton<IConfiguration>(config);
        services.AddDbContextFactory<RdDbContext>(o => o
            .UseSqlServer(conn)
            .AddInterceptors(new AppendOnlyInterceptor()));
        services.AddRdSync(config);

        await using var provider = services.BuildServiceProvider();
        var selectors = args.Skip(1).Select(a => a.ToLowerInvariant()).ToHashSet();
        bool Selected(string name) => selectors.Count == 0 || selectors.Contains(name);

        using var scope = provider.CreateScope();
        var log = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("sync");

        if (Selected("stripe"))
        {
            log.LogInformation("Running Stripe sync…");
            await scope.ServiceProvider.GetRequiredService<StripeSyncJob>().RunAsync(CancellationToken.None);
        }
        if (Selected("meta"))
        {
            log.LogInformation("Running Meta sync…");
            await scope.ServiceProvider.GetRequiredService<MetaSyncJob>().RunAsync(CancellationToken.None);
        }
        if (Selected("ghl"))
        {
            log.LogInformation("Running GHL message sync…");
            await scope.ServiceProvider.GetRequiredService<GhlMessageSyncJob>().RunAsync(CancellationToken.None);
        }
        if (Selected("eval"))
        {
            log.LogInformation("Running policy evaluation…");
            await scope.ServiceProvider.GetRequiredService<PolicyEvaluationJob>().RunAsync(CancellationToken.None);
        }

        log.LogInformation("Done.");
        return 0;
    }
}
