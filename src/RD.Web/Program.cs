using Hangfire;
using Hangfire.SqlServer;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using RD.Domain;
using RD.Infrastructure;
using RD.Infrastructure.Sync;
using RD.Web.Components;
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

builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddScoped<CockpitStateService>();
builder.Services.AddScoped<OutboxActionService>();
builder.Services.AddScoped<ClientDirectoryService>();
builder.Services.AddScoped<ReconciliationService>();

// Lane A gateways + sync jobs + the policy evaluation heartbeat.
builder.Services.AddRdSync(builder.Configuration);

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

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Dashboard: Hangfire's default filter allows local requests only; Operator
// authorization replaces it when Identity lands (M2).
app.MapHangfireDashboard("/hangfire");

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

app.Run();
