using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using RD.Web.Components.Cockpit;
using RD.Web.Services;

namespace RD.Tests.Cockpit;

/// <summary>
/// bUnit render tests for the F6 first-run and all-quiet cockpit states.
/// The states are factored into child components so they render without the
/// page's data loading.
/// </summary>
public class CockpitRenderTests : BunitContext, IAsyncLifetime
{
    public CockpitRenderTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor registers IAsyncDisposable-only services; xunit's sync Dispose
    // would throw, so tear the bUnit container down asynchronously instead.
    async Task IAsyncLifetime.DisposeAsync() => await DisposeAsync();

    private static CockpitSnapshot FirstRunSnapshot(int linked = 268, int total = 646) => new()
    {
        State = CockpitState.FirstRun,
        ComputedAt = DateTimeOffset.UtcNow,
        ExposureByCurrency = [new CurrencyExposure("USD", 0m, 0)],
        ClientsAtRisk = 0,
        CampaignsLive = 0,
        MasterClientCount = 177,
        FailedPaymentsToday = 0,
        OpenActionCount = 0,
        MappingGaps = [],
        SyncFreshness = [],
        ClientsLinked = linked,
        TotalClients = total,
    };

    [Fact]
    public void FirstRun_shows_import_progress_and_guided_next_step()
    {
        var cut = Render<CockpitFirstRun>(p => p.Add(x => x.Snapshot, FirstRunSnapshot()));

        cut.Markup.Should().Contain("268 of 646 clients linked");
        cut.Markup.Should().Contain("First run");

        var cta = cut.Find("a[href='/reconciliation']");
        cta.TextContent.Should().Contain("Start here — fix mappings");
    }

    [Fact]
    public void FirstRun_progress_bar_reflects_linked_ratio()
    {
        var cut = Render<CockpitFirstRun>(p => p.Add(x => x.Snapshot, FirstRunSnapshot(linked: 323, total: 646)));

        // MudProgressLinear exposes the value through aria attributes.
        var bar = cut.Find("[role=progressbar]");
        bar.GetAttribute("aria-valuenow").Should().Be("50");
    }

    [Fact]
    public void AllQuiet_shows_positive_state_with_streak_placeholder()
    {
        var cut = Render<CockpitAllQuiet>();

        cut.Markup.Should().Contain("All quiet — every client paid ✓");
        cut.Markup.Should().Contain("Clean streak");
    }
}
