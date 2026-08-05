using System.Net;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;
using RD.Infrastructure;
using RD.Infrastructure.Email;

namespace RD.Web.Services;

/// <summary>Outcome of a reset attempt. <see cref="Error"/> is the sentence shown to the user.</summary>
public sealed record PasswordResetResult(bool Ok, string? Error);

/// <summary>
/// Self-service password reset: mail a single-use link, then consume it.
///
/// Two rules shape everything here:
///
/// 1. <b>No account enumeration.</b> Requesting a link looks identical whether or
///    not the address belongs to a user — same page, same wording, no timing tell
///    worth chasing. The reset step collapses every failure (unknown user, wrong
///    address, expired or forged token) into one message for the same reason.
/// 2. <b>No silent drops.</b> When the relay isn't configured the UI says so and
///    points at the admin reset path, rather than showing "check your inbox" for
///    mail that will never arrive. Reset links are credentials, so they are never
///    written to logs — not even in Development.
/// </summary>
public sealed class PasswordResetService(
    UserManager<AppUser> users,
    IEmailSender email,
    IMemoryCache cache,
    ILogger<PasswordResetService> logger)
{
    /// <summary>One link per address per window. A public endpoint that sends mail is a mail-bomb primitive otherwise.</summary>
    private static readonly TimeSpan ResendWindow = TimeSpan.FromMinutes(2);

    /// <summary>False ⇒ the forgot-password page tells the user to ask an Admin instead of promising an email.</summary>
    public bool EmailConfigured => email.IsConfigured;

    /// <summary>
    /// Mails a reset link if <paramref name="emailAddress"/> belongs to a user.
    /// Returns nothing: the caller shows the same confirmation either way.
    /// </summary>
    public async Task RequestAsync(string emailAddress, string appBaseUri, CancellationToken ct = default)
    {
        emailAddress = emailAddress.Trim();
        if (string.IsNullOrWhiteSpace(emailAddress) || !email.IsConfigured) return;

        var throttleKey = $"pwreset:{emailAddress.ToUpperInvariant()}";
        if (cache.TryGetValue(throttleKey, out _)) return;
        cache.Set(throttleKey, true, ResendWindow);

        var user = await users.FindByEmailAsync(emailAddress);
        // Unconfirmed addresses are unproven — every user this app creates is
        // confirmed at creation, so this only ever catches a hand-edited row.
        if (user is null || !await users.IsEmailConfirmedAsync(user)) return;

        var token = await users.GeneratePasswordResetTokenAsync(user);
        var code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
        var link = $"{appBaseUri.TrimEnd('/')}/Account/ResetPassword?code={code}";

        try
        {
            await email.SendAsync(user.Email!, "Reset your Rocket Detailer password", Body(link), ct);
        }
        catch (Exception ex)
        {
            // Surfacing this to the caller would leak that the address exists.
            // The operator sees it here; the user sees the neutral confirmation.
            logger.LogError(ex, "Failed to send password-reset email.");
        }
    }

    /// <summary>
    /// Consumes a reset link. Every failure returns the same sentence — an attacker
    /// holding one valid token must not learn which other addresses are real.
    /// </summary>
    public async Task<PasswordResetResult> ResetAsync(string emailAddress, string? encodedCode, string newPassword)
    {
        const string Invalid = "This link is invalid or has expired. Check the email address you entered, or request a new link.";

        if (string.IsNullOrWhiteSpace(encodedCode)) return new PasswordResetResult(false, Invalid);

        string token;
        try
        {
            token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(encodedCode));
        }
        catch (FormatException)
        {
            return new PasswordResetResult(false, Invalid);
        }

        var user = await users.FindByEmailAsync(emailAddress.Trim());
        if (user is null) return new PasswordResetResult(false, Invalid);

        var result = await users.ResetPasswordAsync(user, token, newPassword);
        if (!result.Succeeded)
        {
            // Password-policy complaints are safe to show and are the only reason a
            // user with a good link lands here; token failures stay generic.
            var policyErrors = result.Errors
                .Where(e => !e.Code.Contains("Token", StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Description)
                .ToList();
            return new PasswordResetResult(false, policyErrors.Count > 0 ? string.Join(" ", policyErrors) : Invalid);
        }

        // A lockout earned by failed sign-ins is exactly what drove them here, so
        // clear it. An Admin lock is indefinite (year 9999) and must survive — a
        // password reset is not a way out of being locked out on purpose.
        if (user.LockoutEnd is { } end && end.Year < 9999)
        {
            await users.SetLockoutEndDateAsync(user, null);
            await users.ResetAccessFailedCountAsync(user);
        }

        // ResetPasswordAsync rolls the security stamp, so live cookies for this
        // user fail their next revalidation — a stolen session dies with the reset.
        logger.LogInformation("Password reset completed for {Email}.", user.Email);
        return new PasswordResetResult(true, null);
    }

    private static string Body(string link)
    {
        var href = WebUtility.HtmlEncode(link);
        return $"""
            <p>Someone asked to reset the password for your Rocket Detailer Control Plane account.</p>
            <p><a href="{href}">Choose a new password</a></p>
            <p>Or paste this into your browser:<br><span>{href}</span></p>
            <p>The link works once and expires in two hours. If you didn't ask for it, ignore this email — your password stays as it is.</p>
            """;
    }
}
