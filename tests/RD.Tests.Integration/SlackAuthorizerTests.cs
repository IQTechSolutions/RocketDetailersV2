using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using RD.Infrastructure;
using RD.Infrastructure.Slack;
using RD.Tests.Integration.TestInfra;

namespace RD.Tests.Integration;

/// <summary>
/// OV 15: a Slack signature authenticates the workspace, not a person. The
/// authorizer only clears a Slack user that is claim-linked to an ENABLED internal
/// user holding the Operator (or Admin) role.
/// </summary>
public sealed class SlackAuthorizerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 07, 24, 12, 0, 0, TimeSpan.Zero);
    private readonly SyncTestDb _db = new();
    private readonly SlackAuthorizer _authorizer;

    public SlackAuthorizerTests() => _authorizer = new SlackAuthorizer(_db.Factory, new TestClock(Now));

    [Fact]
    public async Task Operator_linked_by_claim_is_authorized()
    {
        SeedUser("op@rocket.test", slackUserId: "U_OP", role: "Operator");

        var result = await _authorizer.AuthorizeAsync("U_OP");

        result.Authorized.Should().BeTrue();
        result.UserName.Should().Be("op@rocket.test");
    }

    [Fact]
    public async Task Admin_linked_by_claim_is_authorized()
    {
        SeedUser("admin@rocket.test", slackUserId: "U_ADMIN", role: "Admin");

        (await _authorizer.AuthorizeAsync("U_ADMIN")).Authorized.Should().BeTrue();
    }

    [Fact]
    public async Task User_without_operator_role_is_not_authorized()
    {
        SeedUser("viewer@rocket.test", slackUserId: "U_VIEW", role: "Viewer");

        var result = await _authorizer.AuthorizeAsync("U_VIEW");

        result.Authorized.Should().BeFalse();
        result.UserName.Should().BeNull();
    }

    [Fact]
    public async Task Unmapped_slack_user_is_not_authorized()
    {
        // A user exists as an Operator, but this Slack id is linked to nobody.
        SeedUser("op@rocket.test", slackUserId: "U_OP", role: "Operator");

        (await _authorizer.AuthorizeAsync("U_STRANGER")).Authorized.Should().BeFalse();
    }

    [Fact]
    public async Task Locked_out_operator_is_not_authorized()
    {
        SeedUser("locked@rocket.test", slackUserId: "U_LOCK", role: "Operator",
            lockoutEnd: Now.AddHours(1));

        (await _authorizer.AuthorizeAsync("U_LOCK")).Authorized.Should().BeFalse();
    }

    [Fact]
    public async Task Blank_slack_user_is_not_authorized()
        => (await _authorizer.AuthorizeAsync("")).Authorized.Should().BeFalse();

    /// <summary>Seeds an AppUser + its role + the role assignment + a slack:user_id claim, directly in the Identity tables.</summary>
    private void SeedUser(string email, string slackUserId, string role, DateTimeOffset? lockoutEnd = null)
    {
        using var db = _db.CreateContext();

        var roleId = EnsureRole(db, role);

        var user = new AppUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString(),
            LockoutEnd = lockoutEnd,
        };
        db.Users.Add(user);
        db.UserRoles.Add(new IdentityUserRole<string> { UserId = user.Id, RoleId = roleId });
        db.UserClaims.Add(new IdentityUserClaim<string>
        {
            UserId = user.Id,
            ClaimType = SlackClaims.UserId,
            ClaimValue = slackUserId,
        });
        db.SaveChanges();
    }

    private static string EnsureRole(RdDbContext db, string role)
    {
        var existing = db.Roles.FirstOrDefault(r => r.Name == role);
        if (existing is not null) return existing.Id;

        var entity = new IdentityRole
        {
            Id = Guid.NewGuid().ToString(),
            Name = role,
            NormalizedName = role.ToUpperInvariant(),
        };
        db.Roles.Add(entity);
        db.SaveChanges();
        return entity.Id;
    }

    public void Dispose() => _db.Dispose();
}
