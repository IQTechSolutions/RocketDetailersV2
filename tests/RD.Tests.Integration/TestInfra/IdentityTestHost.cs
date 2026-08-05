using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RD.Infrastructure;
using RD.Infrastructure.Email;
using RD.Web.Services;

namespace RD.Tests.Integration.TestInfra;

/// <summary>
/// A real ASP.NET Identity stack over a throwaway <see cref="SyncTestDb"/> — same
/// registrations as Program.cs (EF stores + default token providers + data
/// protection), so password-reset tokens are generated and validated by the
/// production code path rather than a stand-in. Email goes to
/// <see cref="CapturingEmailSender"/>.
/// </summary>
public sealed class IdentityTestHost : IDisposable
{
    private readonly ServiceProvider _services;

    public SyncTestDb Db { get; }
    public CapturingEmailSender Email { get; }

    public IdentityTestHost(bool emailConfigured = true)
    {
        Db = new SyncTestDb();
        Email = new CapturingEmailSender(emailConfigured);

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddDataProtection();
        services.AddMemoryCache();
        services.AddScoped(_ => new RdDbContext(Db.Options));
        services.AddIdentityCore<AppUser>(o => o.SignIn.RequireConfirmedAccount = false)
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<RdDbContext>()
            .AddDefaultTokenProviders();
        services.AddSingleton<IEmailSender>(Email);
        services.AddScoped<PasswordResetService>();

        _services = services.BuildServiceProvider();
    }

    /// <summary>Runs <paramref name="work"/> in its own scope — mirrors one web request.</summary>
    public async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> work)
    {
        using var scope = _services.CreateScope();
        return await work(scope.ServiceProvider);
    }

    public Task<T> WithResetAsync<T>(Func<PasswordResetService, Task<T>> work) =>
        InScopeAsync(sp => work(sp.GetRequiredService<PasswordResetService>()));

    public Task<T> WithUsersAsync<T>(Func<UserManager<AppUser>, Task<T>> work) =>
        InScopeAsync(sp => work(sp.GetRequiredService<UserManager<AppUser>>()));

    /// <summary>Creates a confirmed user, the way every code path in this app does.</summary>
    public Task<AppUser> SeedUserAsync(string email, string password, bool emailConfirmed = true) =>
        WithUsersAsync(async users =>
        {
            var user = new AppUser { UserName = email, Email = email, EmailConfirmed = emailConfirmed };
            var created = await users.CreateAsync(user, password);
            if (!created.Succeeded)
                throw new InvalidOperationException(string.Join("; ", created.Errors.Select(e => e.Description)));
            return user;
        });

    public Task<bool> PasswordWorksAsync(string email, string password) =>
        WithUsersAsync(async users =>
        {
            var user = await users.FindByEmailAsync(email);
            return user is not null && await users.CheckPasswordAsync(user, password);
        });

    public void Dispose()
    {
        _services.Dispose();
        Db.Dispose();
    }
}

/// <summary>Test double for the SMTP sender: records every message instead of relaying it.</summary>
public sealed class CapturingEmailSender(bool configured) : IEmailSender
{
    public List<(string To, string Subject, string Body)> Sent { get; } = [];

    public bool IsConfigured { get; } = configured;

    public Task SendAsync(string toAddress, string subject, string htmlBody, CancellationToken ct = default)
    {
        if (!IsConfigured) throw new InvalidOperationException("Email is not configured.");
        Sent.Add((toAddress, subject, htmlBody));
        return Task.CompletedTask;
    }
}
