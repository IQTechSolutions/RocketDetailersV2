using Hangfire;
using Hangfire.Dashboard;
using Hangfire.SqlServer;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using RD.Domain;
using RD.Infrastructure;
using RD.Infrastructure.Enforcement;
using RD.Infrastructure.Slack;
using RD.Infrastructure.Sync;
using RD.Infrastructure.Webhooks;
using RD.Web.Components;
using RD.Web.Identity;
using RD.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();

// Always query THROUGH the factory — Blazor circuits are long-lived and
// concurrent; never inject RdDbContext directly. The append-only interceptor
// rides along on every context the factory hands out.
builder.Services.AddDbContextFactory<RdDbContext>(options => options
    .UseSqlServer(builder.Configuration.GetConnectionString("RocketDetailers"))
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
builder.Services.AddScoped<OpsService>();
builder.Services.AddSingleton<VendorLinks>();

// Lane A gateways + sync jobs + the policy heartbeat + M2 enforcement services
// (dispatcher, approval CAS, kill switch, state builder, stager).
builder.Services.AddRdSync(builder.Configuration);

// Stripe webhook receiver (signature verify + recoverable inbox).
builder.Services.AddStripeWebhooks(builder.Configuration);

// Slack interactive approve/dismiss (signed callbacks + Slack→Operator mapping).
builder.Services.AddSlack(builder.Configuration);

// Hangfire: same SQL Server database, own schema; recurring jobs only — the
// outbox dispatcher (M2) is the pump for external writes, never fire-and-forget.
var connectionString = builder.Configuration.GetConnectionString("RocketDetailers");
builder.Services.AddHangfire(cfg => cfg
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage(connectionString, new SqlServerStorageOptions { SchemaName = "hangfire" }));
builder.Services.AddHangfireServer();

var app = builder.Build();

// Create the database (if absent) and bring the schema current before anything
// touches it — Hangfire connects on startup, so a fresh/unmigrated database
// would otherwise fail with SQL 4060. Single-tenant, self-hosted, single
// instance: auto-migrate on boot is the pragmatic choice (no migration race).
using (var migrationScope = app.Services.CreateScope())
{
    var factory = migrationScope.ServiceProvider.GetRequiredService<IDbContextFactory<RdDbContext>>();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.MigrateAsync();
}

// Configure the HTTP request pipeline.
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
    .AddInteractiveServerRenderMode();

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
if (!string.IsNullOrEmpty(builder.Configuration["Stripe:ApiKey"]))
    recurring.AddOrUpdate<StripeSyncJob>("stripe-sync", j => j.RunAsync(CancellationToken.None), "*/15 * * * *");
if (!string.IsNullOrEmpty(builder.Configuration["Meta:AccessToken"]))
    recurring.AddOrUpdate<MetaSyncJob>("meta-sync", j => j.RunAsync(CancellationToken.None), "0 * * * *");
if (!string.IsNullOrEmpty(builder.Configuration["Ghl:Locations:0:Token"]))
    recurring.AddOrUpdate<GhlMessageSyncJob>("ghl-message-sync", j => j.RunAsync(CancellationToken.None), "*/15 * * * *");
recurring.AddOrUpdate<PolicyEvaluationJob>("policy-evaluation", j => j.RunAsync(CancellationToken.None), "*/5 * * * *");

// The outbox dispatcher is the single pump for external writes. It is always
// scheduled but harmless until a client is promoted out of Shadow (nothing to
// dispatch) and doubly guarded by the safety profile (TestMode + canary-only).
recurring.AddOrUpdate<OutboxDispatcher>("outbox-dispatch", d => d.RunAsync(CancellationToken.None), "* * * * *");
recurring.AddOrUpdate<GhlDeliveryVerificationJob>("ghl-delivery-verify", j => j.RunAsync(CancellationToken.None), "*/5 * * * *");
if (!string.IsNullOrEmpty(builder.Configuration["Slack:IncomingWebhookUrl"]))
    recurring.AddOrUpdate<SlackNotificationJob>("slack-notify", j => j.RunAsync(CancellationToken.None), "* * * * *");

// Ensure roles + the seed user exist so the app is usable on first run,
// then link the mapped Slack users onto their internal accounts.
await IdentitySeeder.SeedAsync(app.Services);
await SlackUserSeeder.SeedAsync(app.Services);

app.Run();
