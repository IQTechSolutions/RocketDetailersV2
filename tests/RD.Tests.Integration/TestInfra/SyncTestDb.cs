using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using RD.Domain;
using RD.Domain.Entities;
using RD.Infrastructure;

namespace RD.Tests.Integration.TestInfra;

/// <summary>
/// One throwaway LocalDB database per test class instance
/// ("RocketDetailers_Test_" + guid), created in the ctor, dropped on Dispose.
/// The AppendOnlyInterceptor is wired in to mirror production behavior.
/// </summary>
public sealed class SyncTestDb : IDisposable
{
    public string DatabaseName { get; }
    public DbContextOptions<RdDbContext> Options { get; }
    public IDbContextFactory<RdDbContext> Factory { get; }

    /// <summary>
    /// SQL Server to create throwaway test databases on. Defaults to LocalDB for developer machines;
    /// CI sets <c>RD_TEST_SQL</c> (e.g. <c>localhost</c> or <c>(localdb)\MSSQLLocalDB</c>) so the suite
    /// can run wherever a SQL instance is reachable instead of requiring LocalDB specifically.
    /// </summary>
    private static string TestServer =>
        Environment.GetEnvironmentVariable("RD_TEST_SQL") is { Length: > 0 } s ? s : @"(localdb)\MSSQLLocalDB";

    public SyncTestDb()
    {
        DatabaseName = "RocketDetailers_Test_" + Guid.NewGuid().ToString("N");
        var connectionString =
            $@"Server={TestServer};Database={DatabaseName};Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True";

        Options = new DbContextOptionsBuilder<RdDbContext>()
            .UseSqlServer(connectionString)
            .AddInterceptors(new AppendOnlyInterceptor())
            .Options;
        Factory = new SimpleFactory(Options);

        using var db = CreateContext();
        db.Database.EnsureCreated();
    }

    public RdDbContext CreateContext() => new(Options);

    /// <summary>Seeds a client plus one active IdentityLink and returns the client id.</summary>
    public Guid SeedClientWithLink(ExternalSystem system, LinkKind kind, string externalId,
        params (ExternalSystem System, LinkKind Kind, string ExternalId)[] moreLinks)
    {
        using var db = CreateContext();
        var client = new Client
        {
            Id = Guid.NewGuid(),
            BusinessName = "Test Detailing Co",
            ContractType = ContractType.Paid,
            AccountType = AccountType.Master,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Clients.Add(client);
        db.IdentityLinks.Add(NewLink(client.Id, system, kind, externalId));
        foreach (var (moreSystem, moreKind, moreExternalId) in moreLinks)
            db.IdentityLinks.Add(NewLink(client.Id, moreSystem, moreKind, moreExternalId));
        db.SaveChanges();
        return client.Id;
    }

    private static IdentityLink NewLink(Guid clientId, ExternalSystem system, LinkKind kind, string externalId) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        System = system,
        Kind = kind,
        ExternalId = externalId,
        VerifiedAt = DateTimeOffset.UtcNow,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    public void Dispose()
    {
        SqlConnection.ClearAllPools();
        using var db = CreateContext();
        db.Database.EnsureDeleted();
    }

    private sealed class SimpleFactory(DbContextOptions<RdDbContext> options) : IDbContextFactory<RdDbContext>
    {
        public RdDbContext CreateDbContext() => new(options);
    }
}
