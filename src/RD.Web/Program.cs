using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using RD.Domain;
using RD.Infrastructure;
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

app.Run();
