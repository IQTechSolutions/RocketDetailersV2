using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RD.Infrastructure.Slack;

/// <summary>
/// At startup, reflects Slack:UserMap into <c>slack:user_id</c> claims on the
/// mapped AppUsers so <see cref="SlackAuthorizer"/> can resolve a clicking Slack
/// user to an internal Operator. Idempotent: a mapping whose claim already exists
/// is skipped; a mapping to an unknown email is logged and skipped (never fatal).
///
/// The host calls <see cref="SeedAsync"/> AFTER the Identity seeder, so the users
/// the map references already exist. Mirrors the IdentitySeeder shape.
/// </summary>
public static class SlackUserSeeder
{
    public static async Task SeedAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var options = sp.GetRequiredService<IOptions<SlackOptions>>().Value;
        var userManager = sp.GetRequiredService<UserManager<AppUser>>();
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("SlackUserSeeder");

        foreach (var entry in options.UserMap)
        {
            if (string.IsNullOrWhiteSpace(entry.SlackUserId) || string.IsNullOrWhiteSpace(entry.Email))
            {
                logger.LogWarning("Slack:UserMap entry is missing SlackUserId or Email — skipped.");
                continue;
            }

            var user = await userManager.FindByEmailAsync(entry.Email);
            if (user is null)
            {
                logger.LogWarning("Slack:UserMap references unknown user {Email} — no claim seeded.", entry.Email);
                continue;
            }

            var claims = await userManager.GetClaimsAsync(user);
            if (claims.Any(c => c.Type == SlackClaims.UserId && c.Value == entry.SlackUserId))
                continue;

            var result = await userManager.AddClaimAsync(user, new Claim(SlackClaims.UserId, entry.SlackUserId));
            if (result.Succeeded)
                logger.LogInformation("Linked Slack user {SlackUserId} to {Email}.", entry.SlackUserId, entry.Email);
            else
                logger.LogError("Failed to link Slack user to {Email}: {Errors}",
                    entry.Email, string.Join("; ", result.Errors.Select(e => e.Description)));
        }
    }
}
