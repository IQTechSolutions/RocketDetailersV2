using Hangfire;
using Hangfire.Dashboard;
using Hangfire.SqlServer;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using RD.Domain;
using RD.Infrastructure;
using RD.Infrastructure.Email;
using RD.Infrastructure.Enforcement;
using RD.Infrastructure.Reconciliation;
using RD.Infrastructure.Slack;
using RD.Infrastructure.Sync;
using RD.Infrastructure.Webhooks;
using RD.Web.Components;
using RD.Web.Identity;
using RD.Web.Services;

var runMetaShadowComparisonOnce = MetaShadowOneShotMode.IsRequested(args);
var builder = WebApplication.CreateBuilder(MetaShadowOneShotMode.HostArguments(args));
// A UTF-8 BOM can survive as the first character when Windows PowerShell 5.1
// bridges a secret through JSON. SqlClient then sees an invisible prefix on
// the first key and reports that otherwise-valid key as unsupported.
var connectionString = builder.Configuration
    .GetConnectionString("RocketDetailers")
    ?.TrimStart('\uFEFF');
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "ConnectionStrings:RocketDetailers is required. Configure it with user-secrets or the " +
        "ConnectionStrings__RocketDetailers environment variable.");
}

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});

// Always query THROUGH the factory — Blazor circuits are long-lived and
// concurrent; never inject RdDbContext directly. The append-only interceptor
// rides along on every context the factory hands out.
builder.Services.AddDbContextFactory<RdDbContext>(options => options
    .UseSqlServer(connectionString)
    .AddInterceptors(new AppendOnlyInterceptor()));

// ── Identity: cookie auth + roles. Every state-changing enforcement control
// (approve/dismiss, pause via kill switch, promote/verify) requires the
// Operator role; the Stripe webhook stays anonymous (signature-authenticated).
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();
builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ApplicationScheme;
    })
    .AddIdentityCookies();
builder.Services.AddIdentityCore<AppUser>(options => options.SignIn.RequireConfirmedAccount = false)
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<RdDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();
builder.Services.ConfigureApplicationCookie(o =>
{
    o.LoginPath = "/Account/Login";
    o.AccessDeniedPath = "/Account/Login";
});
// Password-reset links are emailed credentials; the default one-day window is
// far longer than anyone needs to read their inbox. Two hours is the only
// consumer of this lifespan today (password reset is the sole default-provider token in use).
builder.Services.Configure<DataProtectionTokenProviderOptions>(o => o.TokenLifespan = TimeSpan.FromHours(2));
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Roles.OperatorPolicy, p => p.RequireRole(Roles.OperatorRoles));

builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddScoped<CockpitStateService>();
builder.Services.AddScoped<OutboxActionService>();
builder.Services.AddScoped<ClientDirectoryService>();
builder.Services.AddScoped<ReconciliationService>();
builder.Services.AddScoped<MappingWizardService>();
builder.Services.AddScoped<AnalyticsService>();
builder.Services.AddScoped<TrialAdminService>();
builder.Services.AddScoped<ConvertService>();
builder.Services.AddScoped<ConvertBillingService>();
builder.Services.AddScoped<PackageAdminService>();
builder.Services.AddScoped<OpsService>();
builder.Services.AddScoped<GhlContactAdminService>();
builder.Services.AddScoped<ClientMergeService>();
builder.Services.AddScoped<LegacyInvestigationCleanup>();
// Registered now, activated in a follow-up release after this binary is the
// rollback baseline. Activating hidden legacy Stripe links before that would
// make rollback to arbitrary-first-customer billing unsafe.
builder.Services.AddScoped<LegacySpreadsheetStripeLinkRepair>();
builder.Services.AddScoped<IdentityAdminService>();
builder.Services.AddScoped<PasswordResetService>();
builder.Services.AddSingleton<VendorLinks>();

// Transactional email (password-reset links) + the in-memory resend throttle
// that keeps the anonymous forgot-password endpoint from becoming a mail bomb.
builder.Services.AddRdEmail(builder.Configuration);
builder.Services.AddMemoryCache();

// Lane A gateways + sync jobs + the policy heartbeat + M2 enforcement services
// (dispatcher, approval CAS, kill switch, state builder, stager).
builder.Services.AddRdSync(builder.Configuration);

// Stripe webhook receiver (signature verify + recoverable inbox).
builder.Services.AddStripeWebhooks(builder.Configuration);

// Slack interactive approve/dismiss (signed callbacks + Slack→Operator mapping).
builder.Services.AddSlack(builder.Configuration);

// Hangfire: same SQL Server database, own schema; recurring jobs only — the
// outbox dispatcher (M2) is the pump for external writes, never fire-and-forget.
builder.Services.AddHangfire(cfg => cfg
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage(connectionString, new SqlServerStorageOptions { SchemaName = "hangfire" }));
builder.Services.AddHangfireServer();

var app = builder.Build();

// Manual observer-only command. It deliberately exits before migrations,
// startup repair, HTTP hosting, Hangfire startup, or recurring-job
// registration. The normal deployment must have applied the schema first.
if (runMetaShadowComparisonOnce)
{
    using var comparisonScope = app.Services.CreateScope();
    var factory = comparisonScope.ServiceProvider.GetRequiredService<IDbContextFactory<RdDbContext>>();
    await using var db = await factory.CreateDbContextAsync();
    var pendingMigrations = (await db.Database.GetPendingMigrationsAsync()).ToArray();
    if (pendingMigrations.Length > 0)
    {
        throw new InvalidOperationException(
            "The live service must apply pending V2 migrations before the one-shot Meta shadow comparison can run.");
    }

    var comparison = comparisonScope.ServiceProvider.GetRequiredService<MetaShadowComparisonService>();
    var report = await comparison.SyncAndCompareAsync(CancellationToken.None);
    Console.WriteLine(MetaShadowOneShotMode.SerializeSummary(report));
    return;
}

// Create the database (if absent) and bring the schema current before anything
// touches it — Hangfire connects on startup, so a fresh/unmigrated database
// would otherwise fail with SQL 4060. Single-tenant, self-hosted, single
// instance: auto-migrate on boot is the pragmatic choice (no migration race).
using (var migrationScope = app.Services.CreateScope())
{
    var factory = migrationScope.ServiceProvider.GetRequiredService<IDbContextFactory<RdDbContext>>();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.MigrateAsync();

    var legacyCleanup = migrationScope.ServiceProvider.GetRequiredService<LegacyInvestigationCleanup>();
    var dismissedCount = await legacyCleanup.RunAsync();
    app.Logger.LogInformation(
        "Legacy Stripe investigation cleanup dismissed {AffectedCount} misclassified item(s).",
        dismissedCount);

    var legacyStripeLinkRepair = migrationScope.ServiceProvider
        .GetRequiredService<LegacySpreadsheetStripeLinkRepair>();
    var repair = await legacyStripeLinkRepair.RunAsync();
    app.Logger.LogInformation(
        "Legacy spreadsheet Stripe link repair matched {MatchedInvestigations} item(s), added {LinksAdded} link(s) across {ClientsChanged} client(s), skipped {ConflictInvestigations} conflicted item(s), invalidated {VerificationsInvalidated} verification(s), demoted {ClientsDemoted} client(s), and superseded {OutboxActionsSuperseded} queued action(s).",
        repair.MatchedInvestigations,
        repair.LinksAdded,
        repair.ClientsChanged,
        repair.ConflictInvestigationsSkipped,
        repair.VerificationsInvalidated,
        repair.ClientsDemoted,
        repair.OutboxActionsSuperseded);
}

// Configure the HTTP request pipeline.
// IIS ARR terminates TLS and proxies to the service over loopback. Apply its
// forwarded scheme before HTTPS redirection so public HTTPS requests do not loop.
app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode(options =>
        options.DisableWebSocketCompression = true);

// Reaching this endpoint proves startup, database migrations, Hangfire, and
// identity seeding all completed. CI polls it after switching the live release.
app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    service = "RD.Web",
    version = typeof(Program).Assembly.GetName().Version?.ToString()
})).AllowAnonymous();

// Signature is verified over the raw body, so this endpoint reads bytes directly.
app.MapStripeWebhook();

// Slack interactivity callback — signed, maps the Slack user to an Operator before acting.
app.MapSlackInteractivity();

// Dashboard: Operators/Admins only.
app.MapHangfireDashboard("/hangfire", new DashboardOptions { Authorization = [new HangfireOperatorFilter()] });

// Recurring jobs — each vendor sync registers only when its credentials are
// configured (a job that fails every cycle for want of config is noise, not
// observability). The policy heartbeat always runs: shadow evaluation off
// whatever projections exist is exactly what the shadow phase is for.
var recurring = app.Services.GetRequiredService<IRecurringJobManager>();
VendorRecurringJobs.Reconcile(recurring, builder.Configuration);
recurring.AddOrUpdate<PolicyEvaluationJob>("policy-evaluation", j => j.RunAsync(CancellationToken.None), "*/5 * * * *");
// Reap conversions billed but never paid (AwaitingPayment past ExpiresAt → Expired). Hourly is ample
// for a multi-day payment window; a late payment after expiry is recovered by the webhook.
recurring.AddOrUpdate<ConvertExpirySweepJob>("convert-expiry-sweep", j => j.RunAsync(CancellationToken.None), "0 * * * *");
// Write the `close` tag on paid conversions (fires the onboarding chain). No-op until
// Convert:CloseTagWriteEnabled is turned on; even then GHL TestMode redirects to the test contact.
recurring.AddOrUpdate<ConvertCloseWriteJob>("convert-close-write", j => j.RunAsync(CancellationToken.None), "*/5 * * * *");

// The outbox dispatcher is the single pump for external writes. It is always
// scheduled but harmless until a client is promoted out of Shadow (nothing to
// dispatch) and doubly guarded by the safety profile (TestMode + canary-only).
recurring.AddOrUpdate<OutboxDispatcher>("outbox-dispatch", d => d.RunAsync(CancellationToken.None), "* * * * *");
recurring.AddOrUpdate<GhlDeliveryVerificationJob>("ghl-delivery-verify", j => j.RunAsync(CancellationToken.None), "*/5 * * * *");

// Ensure roles + the seed user exist so the app is usable on first run,
// then link the mapped Slack users onto their internal accounts.
await IdentitySeeder.SeedAsync(app.Services);
await SlackUserSeeder.SeedAsync(app.Services);

app.Run();
