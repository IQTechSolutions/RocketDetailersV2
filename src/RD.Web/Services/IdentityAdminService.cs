using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RD.Infrastructure;
using RD.Web.Identity;

namespace RD.Web.Services;

/// <summary>One row of the users grid.</summary>
public sealed record UserRow(
    string Id, string? Email, string? UserName, string? FirstName, string? LastName,
    IReadOnlyList<string> Roles, bool LockedOut, bool EmailConfirmed)
{
    /// <summary>"First Last" when either name is set, else null.</summary>
    public string? FullName => string.IsNullOrWhiteSpace($"{FirstName} {LastName}".Trim())
        ? null : $"{FirstName} {LastName}".Trim();
}

/// <summary>One row of the roles grid. <see cref="IsBuiltIn"/> roles are referenced in code and can't be deleted.</summary>
public sealed record RoleRow(string Id, string Name, int UserCount, bool IsBuiltIn);

/// <summary>A claim on a role (type + value).</summary>
public sealed record RoleClaimRow(string Type, string Value);

/// <summary>Result of an admin write — success flag plus a human sentence for the snackbar.</summary>
public sealed record AdminResult(bool Ok, string Message);

/// <summary>
/// Admin-only management of ASP.NET Identity: users (create, roles, password,
/// lockout, delete), roles (create, delete), and claims on roles. Wraps
/// UserManager/RoleManager and adds the guards that keep an admin from locking
/// themselves — or the whole org — out: you can't delete or lock your own
/// account, and you can't remove the last Admin. Gated to the Admin role at every
/// call site; this service assumes the caller is already authorized.
/// </summary>
public sealed class IdentityAdminService(UserManager<AppUser> users, RoleManager<IdentityRole> roles)
{
    private static readonly string[] BuiltInRoles = [Roles.Admin, Roles.Operator, Roles.Viewer];

    // ── Users ────────────────────────────────────────────────────────────────

    public async Task<List<UserRow>> ListUsersAsync(CancellationToken ct = default)
    {
        var list = await users.Users.OrderBy(u => u.Email).ToListAsync(ct);
        var rows = new List<UserRow>(list.Count);
        foreach (var u in list)
        {
            var userRoles = await users.GetRolesAsync(u);
            rows.Add(new UserRow(u.Id, u.Email, u.UserName, u.FirstName, u.LastName,
                userRoles.OrderBy(r => r).ToList(), await users.IsLockedOutAsync(u), u.EmailConfirmed));
        }
        return rows;
    }

    public async Task<AdminResult> CreateUserAsync(
        string email, string password, IEnumerable<string> assignRoles, string? firstName = null, string? lastName = null)
    {
        email = email.Trim();
        if (string.IsNullOrWhiteSpace(email)) return new AdminResult(false, "An email is required.");
        if (await users.FindByEmailAsync(email) is not null) return new AdminResult(false, "A user with that email already exists.");

        var user = new AppUser
        {
            UserName = email, Email = email, EmailConfirmed = true,
            FirstName = Clean(firstName), LastName = Clean(lastName),
        };
        var created = await users.CreateAsync(user, password);
        if (!created.Succeeded) return new AdminResult(false, Describe(created));

        var toAdd = assignRoles.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct().ToList();
        if (toAdd.Count > 0)
        {
            var added = await users.AddToRolesAsync(user, toAdd);
            if (!added.Succeeded) return new AdminResult(false, $"User created, but roles failed: {Describe(added)}");
        }
        return new AdminResult(true, $"Created {email}.");
    }

    public async Task<AdminResult> SetProfileAsync(string userId, string? firstName, string? lastName)
    {
        var user = await users.FindByIdAsync(userId);
        if (user is null) return new AdminResult(false, "User not found.");

        user.FirstName = Clean(firstName);
        user.LastName = Clean(lastName);
        var result = await users.UpdateAsync(user);
        return result.Succeeded
            ? new AdminResult(true, $"Name updated for {user.Email}.")
            : new AdminResult(false, Describe(result));
    }

    public async Task<AdminResult> SetUserRolesAsync(string userId, IEnumerable<string> desiredRoles, string currentUserId)
    {
        var user = await users.FindByIdAsync(userId);
        if (user is null) return new AdminResult(false, "User not found.");

        var current = (await users.GetRolesAsync(user)).ToHashSet();
        var desired = desiredRoles.Where(r => !string.IsNullOrWhiteSpace(r)).ToHashSet();

        var removing = current.Except(desired).ToList();
        var adding = desired.Except(current).ToList();

        // Guard: don't let the org lose its last Admin, and don't let an admin strip their own Admin.
        if (removing.Contains(Roles.Admin))
        {
            if (userId == currentUserId) return new AdminResult(false, "You can't remove your own Admin role.");
            if (await IsLastAdminAsync(user)) return new AdminResult(false, "This is the last Admin — assign Admin to someone else first.");
        }

        if (removing.Count > 0)
        {
            var r = await users.RemoveFromRolesAsync(user, removing);
            if (!r.Succeeded) return new AdminResult(false, Describe(r));
        }
        if (adding.Count > 0)
        {
            var a = await users.AddToRolesAsync(user, adding);
            if (!a.Succeeded) return new AdminResult(false, Describe(a));
        }
        return new AdminResult(true, $"Roles updated for {user.Email}.");
    }

    public async Task<AdminResult> SetPasswordAsync(string userId, string newPassword)
    {
        var user = await users.FindByIdAsync(userId);
        if (user is null) return new AdminResult(false, "User not found.");

        // Admin reset: drop any existing password and set the new one (no current-password check).
        if (await users.HasPasswordAsync(user))
        {
            var removed = await users.RemovePasswordAsync(user);
            if (!removed.Succeeded) return new AdminResult(false, Describe(removed));
        }
        var added = await users.AddPasswordAsync(user, newPassword);
        return added.Succeeded
            ? new AdminResult(true, $"Password set for {user.Email}.")
            : new AdminResult(false, Describe(added));
    }

    public async Task<AdminResult> SetLockoutAsync(string userId, bool locked, string currentUserId)
    {
        var user = await users.FindByIdAsync(userId);
        if (user is null) return new AdminResult(false, "User not found.");

        if (locked)
        {
            if (userId == currentUserId) return new AdminResult(false, "You can't lock your own account.");
            if (await users.IsInRoleAsync(user, Roles.Admin) && await IsLastAdminAsync(user))
                return new AdminResult(false, "This is the last Admin — you can't lock them out.");
        }

        await users.SetLockoutEnabledAsync(user, true);
        var result = await users.SetLockoutEndDateAsync(user, locked ? DateTimeOffset.MaxValue : null);
        return result.Succeeded
            ? new AdminResult(true, locked ? $"{user.Email} is locked out." : $"{user.Email} is unlocked.")
            : new AdminResult(false, Describe(result));
    }

    public async Task<AdminResult> DeleteUserAsync(string userId, string currentUserId)
    {
        if (userId == currentUserId) return new AdminResult(false, "You can't delete your own account.");
        var user = await users.FindByIdAsync(userId);
        if (user is null) return new AdminResult(false, "User not found.");
        if (await users.IsInRoleAsync(user, Roles.Admin) && await IsLastAdminAsync(user))
            return new AdminResult(false, "This is the last Admin — you can't delete them.");

        var result = await users.DeleteAsync(user);
        return result.Succeeded
            ? new AdminResult(true, $"Deleted {user.Email}.")
            : new AdminResult(false, Describe(result));
    }

    // ── Roles ────────────────────────────────────────────────────────────────

    public async Task<List<string>> AllRoleNamesAsync(CancellationToken ct = default) =>
        await roles.Roles.Select(r => r.Name!).OrderBy(n => n).ToListAsync(ct);

    public async Task<List<RoleRow>> ListRolesAsync(CancellationToken ct = default)
    {
        var all = await roles.Roles.OrderBy(r => r.Name).ToListAsync(ct);
        var rows = new List<RoleRow>(all.Count);
        foreach (var r in all)
        {
            var count = (await users.GetUsersInRoleAsync(r.Name!)).Count;
            rows.Add(new RoleRow(r.Id, r.Name!, count, BuiltInRoles.Contains(r.Name)));
        }
        return rows;
    }

    public async Task<AdminResult> CreateRoleAsync(string name)
    {
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name)) return new AdminResult(false, "A role name is required.");
        if (await roles.RoleExistsAsync(name)) return new AdminResult(false, "That role already exists.");
        var result = await roles.CreateAsync(new IdentityRole(name));
        return result.Succeeded ? new AdminResult(true, $"Created role {name}.") : new AdminResult(false, Describe(result));
    }

    public async Task<AdminResult> DeleteRoleAsync(string roleId)
    {
        var role = await roles.FindByIdAsync(roleId);
        if (role is null) return new AdminResult(false, "Role not found.");
        if (BuiltInRoles.Contains(role.Name)) return new AdminResult(false, $"{role.Name} is a built-in role and can't be deleted.");
        var result = await roles.DeleteAsync(role);
        return result.Succeeded ? new AdminResult(true, $"Deleted role {role.Name}.") : new AdminResult(false, Describe(result));
    }

    // ── Claims on roles ───────────────────────────────────────────────────────

    public async Task<List<RoleClaimRow>> ListRoleClaimsAsync(string roleId)
    {
        var role = await roles.FindByIdAsync(roleId);
        if (role is null) return [];
        var claims = await roles.GetClaimsAsync(role);
        return claims.Select(c => new RoleClaimRow(c.Type, c.Value)).OrderBy(c => c.Type).ThenBy(c => c.Value).ToList();
    }

    public async Task<AdminResult> AddRoleClaimAsync(string roleId, string type, string value)
    {
        type = type.Trim();
        value = value.Trim();
        if (string.IsNullOrWhiteSpace(type)) return new AdminResult(false, "A claim type is required.");
        var role = await roles.FindByIdAsync(roleId);
        if (role is null) return new AdminResult(false, "Role not found.");
        var existing = await roles.GetClaimsAsync(role);
        if (existing.Any(c => c.Type == type && c.Value == value)) return new AdminResult(false, "That claim is already on the role.");
        var result = await roles.AddClaimAsync(role, new Claim(type, value));
        return result.Succeeded ? new AdminResult(true, "Claim added.") : new AdminResult(false, Describe(result));
    }

    public async Task<AdminResult> RemoveRoleClaimAsync(string roleId, string type, string value)
    {
        var role = await roles.FindByIdAsync(roleId);
        if (role is null) return new AdminResult(false, "Role not found.");
        var result = await roles.RemoveClaimAsync(role, new Claim(type, value));
        return result.Succeeded ? new AdminResult(true, "Claim removed.") : new AdminResult(false, Describe(result));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<bool> IsLastAdminAsync(AppUser candidate)
    {
        var admins = await users.GetUsersInRoleAsync(Roles.Admin);
        return admins.Count <= 1 && admins.Any(a => a.Id == candidate.Id);
    }

    private static string Describe(IdentityResult result) =>
        string.Join("; ", result.Errors.Select(e => e.Description));

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
