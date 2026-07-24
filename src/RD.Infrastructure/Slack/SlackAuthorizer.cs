using Microsoft.EntityFrameworkCore;
using RD.Domain;

namespace RD.Infrastructure.Slack;

/// <summary>Outcome of mapping a Slack user id to an authorized internal Operator.</summary>
public sealed record SlackAuthorizationResult(bool Authorized, string? UserName);

/// <summary>
/// Enforces outside-voice rule OV item 15: a Slack signature authenticates the
/// WORKSPACE, not a person. Before any action we resolve the clicking Slack user
/// id to an internal AppUser (via the <c>slack:user_id</c> claim), confirm the
/// account is enabled, and require the Operator or Admin role — the same gate the
/// cockpit uses. An unmapped Slack user, a locked-out account, or a user without
/// an operator role is NOT authorized.
///
/// Roles are checked directly against the Identity tables via the DbContext, so
/// this needs no UserManager and stays a plain scoped service over the factory.
/// </summary>
public sealed class SlackAuthorizer(IDbContextFactory<RdDbContext> dbFactory, IClock clock)
{
    // Mirrors RD.Web.Identity.Roles.OperatorRoles — RD.Infrastructure cannot reference RD.Web,
    // so the two operator-role names are pinned here. Keep in sync with IdentitySetup.Roles.
    private static readonly string[] OperatorRoles = ["Operator", "Admin"];

    public async Task<SlackAuthorizationResult> AuthorizeAsync(string? slackUserId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(slackUserId))
            return new SlackAuthorizationResult(false, null);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // 1) Slack user id → internal user id, via the claim the seeder applied.
        var userId = await db.UserClaims
            .Where(c => c.ClaimType == SlackClaims.UserId && c.ClaimValue == slackUserId)
            .Select(c => c.UserId)
            .FirstOrDefaultAsync(ct);
        if (userId is null)
            return new SlackAuthorizationResult(false, null);

        // 2) The user must still exist and be enabled (not currently locked out).
        var user = await db.Users
            .Where(u => u.Id == userId)
            .Select(u => new { u.UserName, u.Email, u.LockoutEnd })
            .FirstOrDefaultAsync(ct);
        if (user is null)
            return new SlackAuthorizationResult(false, null);
        if (user.LockoutEnd is { } lockoutEnd && lockoutEnd > clock.UtcNow)
            return new SlackAuthorizationResult(false, null);

        // 3) Operator claim (role) checked per action.
        var isOperator = await (
            from ur in db.UserRoles
            join r in db.Roles on ur.RoleId equals r.Id
            where ur.UserId == userId && OperatorRoles.Contains(r.Name)
            select r.Id).AnyAsync(ct);
        if (!isOperator)
            return new SlackAuthorizationResult(false, null);

        return new SlackAuthorizationResult(true, user.UserName ?? user.Email ?? slackUserId);
    }
}
