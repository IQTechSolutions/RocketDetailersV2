using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using RD.Domain;
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
    public void FirstRun_import_summary_uses_the_real_client_count()
    {
        var cut = Render<CockpitFirstRun>(p => p.Add(x => x.Snapshot, FirstRunSnapshot(linked: 4, total: 12)));

        cut.Markup.Should().Contain("Your 12 imported clients are here");
    }

    [Fact]
    public void FirstRun_import_summary_reads_sensibly_on_an_empty_deployment()
    {
        var cut = Render<CockpitFirstRun>(p => p.Add(x => x.Snapshot, FirstRunSnapshot(linked: 0, total: 0)));

        cut.Markup.Should().Contain("No clients have been imported yet");
        cut.Markup.Should().NotContain("imported clients are here");
        cut.Markup.Should().Contain("0 of 0 clients linked");

        // The divide-by-zero guard: an empty roster must render an empty bar, not NaN.
        cut.Find("[role=progressbar]").GetAttribute("aria-valuenow").Should().Be("0");
    }

    [Fact]
    public void FirstRun_empty_deployment_points_at_importing_not_reconciliation()
    {
        var cut = Render<CockpitFirstRun>(p => p.Add(x => x.Snapshot, FirstRunSnapshot(linked: 0, total: 0)));

        // Nothing to reconcile on an empty install — the mappings CTA must be gone.
        cut.FindAll("a[href='/reconciliation']").Should().BeEmpty();
        cut.Markup.Should().NotContain("fix identity mappings");

        cut.Markup.Should().Contain("import the client roster");

        // --conn must stay in the displayed command: without it the importer silently
        // loads the roster into a local LocalDB and reports success (Program.cs:83).
        cut.Find("code").TextContent.Should()
            .Be("dotnet run --project src/RD.Tools.Import -- \"<xlsx path>\" --conn \"<connection string>\"");
    }

    [Fact]
    public void FirstRun_keeps_the_mappings_CTA_once_clients_exist()
    {
        var cut = Render<CockpitFirstRun>(p => p.Add(x => x.Snapshot, FirstRunSnapshot(linked: 0, total: 12)));

        cut.Find("a[href='/reconciliation']").TextContent.Should().Contain("Start here — fix mappings");
        cut.FindAll("code").Should().BeEmpty();
    }

    [Fact]
    public void FirstRun_import_summary_is_singular_for_one_client()
    {
        var cut = Render<CockpitFirstRun>(p => p.Add(x => x.Snapshot, FirstRunSnapshot(linked: 0, total: 1)));

        cut.Markup.Should().Contain("Your 1 imported client is here");
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

    [Fact]
    public void Investigation_panel_uses_queue_language_and_counts_open_items()
    {
        var cut = Render<MappingGapsPanel>(parameters => parameters.Add(
            component => component.Gaps,
            [new MappingGap(InvestigationKind.ExternallyPausedPayment, 3)]));

        cut.Markup.Should().Contain("Open investigations");
        cut.Markup.Should().NotContain("Mapping gaps");
        cut.Markup.Should().Contain("3 open items");
        cut.Markup.Should().NotContain("3 clients");
        cut.Markup.Should().Contain("recorded external campaign pause for a paid-up client");
        cut.Markup.Should().Contain("Opened when a linked campaign was reported paused outside the app");
        cut.Markup.Should().Contain("Review items");
        cut.Markup.Should().NotContain("payment arrived for externally-paused ads");
        cut.Markup.Should().NotContain("A human paused these ads");
        cut.Markup.Should().NotContain("Fix these");
    }
}
