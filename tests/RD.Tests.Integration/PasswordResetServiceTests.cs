using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using RD.Tests.Integration.TestInfra;
using RD.Web.Services;

namespace RD.Tests.Integration;

/// <summary>
/// Self-service password reset, exercised against a real Identity stack so the
/// tokens are the production ones. Two properties matter most and are asserted
/// from several angles: the flow never reveals which addresses have accounts, and
/// a link is good exactly once.
/// </summary>
public sealed class PasswordResetServiceTests : IDisposable
{
    private const string Email = "operator@rocket.test";
    private const string OldPassword = "Old-Passw0rd!";
    private const string NewPassword = "New-Passw0rd!";
    private const string BaseUri = "https://control.rocket.test/";

    private readonly IdentityTestHost _host = new();

    public void Dispose() => _host.Dispose();

    // ── Requesting a link ─────────────────────────────────────────────────────

    [Fact]
    public async Task Request_for_a_known_user_emails_a_reset_link()
    {
        await _host.SeedUserAsync(Email, OldPassword);

        await RequestAsync(Email);

        _host.Email.Sent.Should().ContainSingle();
        var (to, subject, body) = _host.Email.Sent[0];
        to.Should().Be(Email);
        subject.Should().Contain("Reset");
        body.Should().Contain($"{BaseUri.TrimEnd('/')}/Account/ResetPassword?code=");
    }

    [Fact]
    public async Task Request_for_an_unknown_address_sends_nothing_and_does_not_throw()
    {
        await _host.SeedUserAsync(Email, OldPassword);

        await RequestAsync("nobody@rocket.test");

        _host.Email.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Request_for_an_unconfirmed_address_sends_nothing()
    {
        await _host.SeedUserAsync("unconfirmed@rocket.test", OldPassword, emailConfirmed: false);

        await RequestAsync("unconfirmed@rocket.test");

        _host.Email.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Repeat_requests_inside_the_resend_window_send_one_email()
    {
        await _host.SeedUserAsync(Email, OldPassword);

        await RequestAsync(Email);
        await RequestAsync(Email);
        await RequestAsync(Email);

        // A public endpoint that sends mail on demand is a mail bomb without this.
        _host.Email.Sent.Should().ContainSingle();
    }

    [Fact]
    public async Task Throttle_is_case_insensitive_on_the_address()
    {
        await _host.SeedUserAsync(Email, OldPassword);

        await RequestAsync(Email);
        await RequestAsync(Email.ToUpperInvariant());

        _host.Email.Sent.Should().ContainSingle();
    }

    [Fact]
    public async Task Request_is_a_no_op_when_email_is_not_configured()
    {
        using var host = new IdentityTestHost(emailConfigured: false);
        await host.SeedUserAsync(Email, OldPassword);

        await host.WithResetAsync(async svc =>
        {
            svc.EmailConfigured.Should().BeFalse();
            await svc.RequestAsync(Email, BaseUri);
            return true;
        });

        host.Email.Sent.Should().BeEmpty();
    }

    // ── Consuming a link ──────────────────────────────────────────────────────

    [Fact]
    public async Task Emailed_link_sets_the_new_password_and_retires_the_old_one()
    {
        await _host.SeedUserAsync(Email, OldPassword);
        await RequestAsync(Email);

        var result = await ResetAsync(Email, CapturedCode(), NewPassword);

        result.Ok.Should().BeTrue();
        (await _host.PasswordWorksAsync(Email, NewPassword)).Should().BeTrue();
        (await _host.PasswordWorksAsync(Email, OldPassword)).Should().BeFalse();
    }

    [Fact]
    public async Task A_link_works_exactly_once()
    {
        await _host.SeedUserAsync(Email, OldPassword);
        await RequestAsync(Email);
        var code = CapturedCode();

        (await ResetAsync(Email, code, NewPassword)).Ok.Should().BeTrue();
        var second = await ResetAsync(Email, code, "Third-Passw0rd!");

        // ResetPasswordAsync rolls the security stamp, so the token no longer validates.
        second.Ok.Should().BeFalse();
        (await _host.PasswordWorksAsync(Email, NewPassword)).Should().BeTrue();
    }

    [Fact]
    public async Task A_token_minted_for_one_user_cannot_reset_another()
    {
        await _host.SeedUserAsync(Email, OldPassword);
        await _host.SeedUserAsync("victim@rocket.test", OldPassword);
        await RequestAsync(Email);

        var result = await ResetAsync("victim@rocket.test", CapturedCode(), NewPassword);

        result.Ok.Should().BeFalse();
        (await _host.PasswordWorksAsync("victim@rocket.test", OldPassword)).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-base64url-!!!")]
    [InlineData("Zm9yZ2Vk")] // well-formed base64url, meaningless token
    public async Task Missing_or_forged_codes_are_refused(string? code)
    {
        await _host.SeedUserAsync(Email, OldPassword);

        var result = await ResetAsync(Email, code, NewPassword);

        result.Ok.Should().BeFalse();
        (await _host.PasswordWorksAsync(Email, OldPassword)).Should().BeTrue();
    }

    [Fact]
    public async Task Unknown_address_and_bad_token_are_indistinguishable()
    {
        await _host.SeedUserAsync(Email, OldPassword);
        await RequestAsync(Email);
        var code = CapturedCode();

        var unknownUser = await ResetAsync("nobody@rocket.test", code, NewPassword);
        var badToken = await ResetAsync(Email, "Zm9yZ2Vk", NewPassword);

        // Same sentence either way: holding one valid link must not be an oracle
        // for which other addresses have accounts.
        unknownUser.Ok.Should().BeFalse();
        badToken.Ok.Should().BeFalse();
        unknownUser.Error.Should().Be(badToken.Error);
    }

    [Fact]
    public async Task A_password_that_fails_policy_says_why_and_leaves_the_link_usable()
    {
        await _host.SeedUserAsync(Email, OldPassword);
        await RequestAsync(Email);
        var code = CapturedCode();

        var rejected = await ResetAsync(Email, code, "short");

        rejected.Ok.Should().BeFalse();
        rejected.Error.Should().NotBeNull();
        rejected.Error!.Should().NotContain("expired");
        // The token was never consumed, so the user can retry with a better password.
        (await ResetAsync(Email, code, NewPassword)).Ok.Should().BeTrue();
    }

    // ── Lockout interaction ───────────────────────────────────────────────────

    [Fact]
    public async Task Reset_clears_a_lockout_earned_by_failed_sign_ins()
    {
        await _host.SeedUserAsync(Email, OldPassword);
        await SetLockoutAsync(DateTimeOffset.UtcNow.AddMinutes(5));
        await RequestAsync(Email);

        (await ResetAsync(Email, CapturedCode(), NewPassword)).Ok.Should().BeTrue();

        // Forgetting the password is exactly what caused the lockout; clearing it
        // is the difference between a working reset and a five-minute stare.
        (await IsLockedOutAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Reset_does_not_lift_an_administrative_lockout()
    {
        await _host.SeedUserAsync(Email, OldPassword);
        await SetLockoutAsync(DateTimeOffset.MaxValue);
        await RequestAsync(Email);

        (await ResetAsync(Email, CapturedCode(), NewPassword)).Ok.Should().BeTrue();

        // An Admin locked this account on purpose. A password reset is not a way out.
        (await IsLockedOutAsync()).Should().BeTrue();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private Task RequestAsync(string email) =>
        _host.WithResetAsync(async svc => { await svc.RequestAsync(email, BaseUri); return true; });

    private Task<PasswordResetResult> ResetAsync(string email, string? code, string password) =>
        _host.WithResetAsync(svc => svc.ResetAsync(email, code, password));

    private Task<bool> SetLockoutAsync(DateTimeOffset until) =>
        _host.WithUsersAsync(async users =>
        {
            var user = await users.FindByEmailAsync(Email);
            await users.SetLockoutEnabledAsync(user!, true);
            await users.SetLockoutEndDateAsync(user!, until);
            return true;
        });

    private Task<bool> IsLockedOutAsync() =>
        _host.WithUsersAsync(async users => await users.IsLockedOutAsync((await users.FindByEmailAsync(Email))!));

    /// <summary>The base64url token out of the most recent email — what the user's click carries.</summary>
    private string CapturedCode()
    {
        var body = _host.Email.Sent[^1].Body;
        var match = Regex.Match(body, @"code=([A-Za-z0-9\-_]+)");
        match.Success.Should().BeTrue("the reset email must contain a code= link");
        return match.Groups[1].Value;
    }
}
