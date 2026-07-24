using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace RD.Infrastructure;

/// <summary>Design-time only (migrations). Runtime connection strings come from configuration/user-secrets.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<RdDbContext>
{
    public RdDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<RdDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=RocketDetailers;Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True")
            .AddInterceptors(new AppendOnlyInterceptor())
            .Options;
        return new RdDbContext(options);
    }
}
