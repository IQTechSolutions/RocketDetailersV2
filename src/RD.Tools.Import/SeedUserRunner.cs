using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RD.Infrastructure;

namespace RD.Tools.Import;

/// <summary>
/// One-shot admin seeder that talks straight to the configured database with a
/// minimal Identity stack (no web host, no Hangfire) — reliable for seeding
/// against a remote SQL Server. Uses the same UserManager/PasswordHasher the
/// app uses, so the created credentials work at the login page.
///
///   dotnet run --project src/RD.Tools.Import -- seed-user &lt;email&gt; &lt;password&gt; [role]
///
/// Connection string comes from the RD_CONN environment variable (never
/// committed). Role defaults to Admin; roles Admin/Operator/Viewer are ensured.
/// </summary>
public static class SeedUserRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        // args: [0]="seed-user", [1]=email, [2]=password, [3]=role?
        if (args.Length < 3)
        {
            Console.WriteLine("Usage: seed-user <email> <password> [role]   (RD_CONN env = connection string)");
            return 1;
        }
        var email = args[1];
        var password = args[2];
        var role = args.Length > 3 ? args[3] : "Admin";

        var conn = Environment.GetEnvironmentVariable("RD_CONN");
        if (string.IsNullOrWhiteSpace(conn))
        {
            Console.WriteLine("ERROR: set the RD_CONN environment variable to the target connection string.");
            return 1;
        }

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
        services.AddDbContext<RdDbContext>(o => o.UseSqlServer(conn).AddInterceptors(new AppendOnlyInterceptor()));
        services.AddIdentityCore<AppUser>(o => o.SignIn.RequireConfirmedAccount = false)
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<RdDbContext>();

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;
        var roleManager = sp.GetRequiredService<RoleManager<IdentityRole>>();
        var userManager = sp.GetRequiredService<UserManager<AppUser>>();

        foreach (var r in new[] { "Admin", "Operator", "Viewer" })
            if (!await roleManager.RoleExistsAsync(r))
                await roleManager.CreateAsync(new IdentityRole(r));

        var user = await userManager.FindByEmailAsync(email);
        if (user is null)
        {
            user = new AppUser { UserName = email, Email = email, EmailConfirmed = true };
            var created = await userManager.CreateAsync(user, password);
            if (!created.Succeeded)
            {
                Console.WriteLine("Failed to create user: " + string.Join("; ", created.Errors.Select(e => e.Description)));
                return 1;
            }
            Console.WriteLine($"Created user {email}.");
        }
        else
        {
            // Reset the password so a re-run is idempotent and can fix a forgotten one.
            var token = await userManager.GeneratePasswordResetTokenAsync(user);
            var reset = await userManager.ResetPasswordAsync(user, token, password);
            if (!reset.Succeeded)
            {
                Console.WriteLine("User exists but password reset failed: " + string.Join("; ", reset.Errors.Select(e => e.Description)));
                return 1;
            }
            Console.WriteLine($"User {email} already existed — password reset.");
        }

        if (!await userManager.IsInRoleAsync(user, role))
            await userManager.AddToRoleAsync(user, role);

        Console.WriteLine($"Done. {email} is in role {role}.");
        return 0;
    }
}
