using Hangfire.Dashboard;
using Microsoft.AspNetCore.Identity;
using RD.Infrastructure;

namespace RD.Web.Identity;

/// <summary>Role names. Operator can act (approve, pause, promote, kill switch); Admin additionally manages users; Viewer is read-only.</summary>
public static class Roles
{
    public const string Admin = "Admin";
    public const string Operator = "Operator";
    public const string Viewer = "Viewer";

    /// <summary>The policy that gates every state-changing enforcement control.</summary>
    public const string OperatorPolicy = "OperatorOnly";
    public static readonly string[] OperatorRoles = [Operator, Admin];
    /// <summary>For &lt;AuthorizeView Roles="..."&gt; which takes a comma-separated string.</summary>
    public const string OperatorRolesCsv = "Operator,Admin";
}

/// <summary>Seeds the three roles and, if configured, an initial user so the app is usable on first run.</summary>
public static class IdentitySeeder
{
    public static async Task SeedAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var roleManager = sp.GetRequiredService<RoleManager<IdentityRole>>();
        var userManager = sp.GetRequiredService<UserManager<AppUser>>();
        var config = sp.GetRequiredService<IConfiguration>();
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("IdentitySeeder");

        foreach (var role in new[] { Roles.Admin, Roles.Operator, Roles.Viewer })
            if (!await roleManager.RoleExistsAsync(role))
                await roleManager.CreateAsync(new IdentityRole(role));

        var email = config["Identity:Seed:Email"];
        var password = config["Identity:Seed:Password"];
        var seedRole = config["Identity:Seed:Role"] ?? Roles.Admin;
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            logger.LogWarning("No Identity:Seed user configured — create one before the app can be signed into.");
            return;
        }

        var user = await userManager.FindByEmailAsync(email);
        if (user is null)
        {
            user = new AppUser { UserName = email, Email = email, EmailConfirmed = true };
            var created = await userManager.CreateAsync(user, password);
            if (!created.Succeeded)
            {
                logger.LogError("Failed to seed user {Email}: {Errors}", email, string.Join("; ", created.Errors.Select(e => e.Description)));
                return;
            }
        }
        if (!await userManager.IsInRoleAsync(user, seedRole))
            await userManager.AddToRoleAsync(user, seedRole);
        logger.LogInformation("Seed user {Email} ensured with role {Role}.", email, seedRole);
    }
}

/// <summary>The Hangfire dashboard is an operational surface — Operators/Admins only.</summary>
public sealed class HangfireOperatorFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var user = context.GetHttpContext().User;
        return user.Identity?.IsAuthenticated == true && Roles.OperatorRoles.Any(user.IsInRole);
    }
}
