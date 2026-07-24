using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using RD.Domain;
using RD.Web.Components.Analytics;
using RD.Web.Services;

namespace RD.Tests.Analytics;

/// <summary>
/// bUnit render tests for the M3 analytics dashboard. The body and the net-cash
/// card are factored into child components that render from a stubbed snapshot,
/// so they render without the page's DB loading (mirrors CockpitRenderTests).
/// </summary>
public class AnalyticsRenderTests : BunitContext, IAsyncLifetime
{
    public AnalyticsRenderTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor registers IAsyncDisposable-only services; tear the container down asynchronously.
    async Task IAsyncLifetime.DisposeAsync() => await DisposeAsync();

    /// <summary>A snapshot with no ledger money — the page's empty-state path.</summary>
    private static AnalyticsSnapshot EmptySnapshot() => new()
    {
        ComputedAt = DateTimeOffset.UtcNow,
        NetPositionByCurrency = [new CurrencyRollup("USD", 0m, 0m, 0m, 0m, 0, 0)],
        ClientNetPositions = [],
        ExposureByCurrency = [new CurrencyExposure("USD", 0m, 0)],
        PackageRevenue = [],
        EnforcementActivity = new EnforcementActivity([], []),
        MappingGaps = [],
        MasterClientCount = 177,
        VerifiedMasterClientCount = 0,
        ClientSegments = [],
        ModeMix = [],
    };

    [Fact]
    public void Empty_state_tells_the_owner_analytics_fill_in_as_the_ledger_grows()
    {
        var cut = Render<AnalyticsBody>(p => p.Add(x => x.Snapshot, EmptySnapshot()));

        cut.Markup.Should().Contain("No paid charges recorded yet");
        cut.Markup.Should().Contain("Analytics fill in as the ledger grows");
        // Honest about the receivables-vs-spend nature.
        cut.Markup.Should().Contain("not yet reconciled");
    }

    [Fact]
    public void Populated_net_cash_card_shows_a_positive_position_in_green()
    {
        // +$100 net: $300 paid, $200 ad spend, no exposure.
        var rollup = new CurrencyRollup("USD", NetPosition: 100m, Paid: 300m,
            AdSpendCost: 200m, Exposure: 0m, ClientCount: 1, NegativeClientCount: 0);

        var cut = Render<AnalyticsNetCashCard>(p => p.Add(x => x.Rollup, rollup));

        cut.Markup.Should().Contain("Net cash position");
        cut.Markup.Should().Contain("+$100");
        cut.Markup.Should().Contain("$300"); // charges paid
        cut.Markup.Should().Contain("$200"); // ad spend
        cut.Markup.Should().Contain("1 client with activity");
    }

    [Fact]
    public void Negative_net_cash_card_shows_the_shortfall_in_red()
    {
        // −$150 net: $100 paid, $250 ad spend, $150 exposure, one underwater client.
        var rollup = new CurrencyRollup("CAD", NetPosition: -150m, Paid: 100m,
            AdSpendCost: 250m, Exposure: 150m, ClientCount: 1, NegativeClientCount: 1);

        var cut = Render<AnalyticsNetCashCard>(p => p.Add(x => x.Rollup, rollup));

        cut.Markup.Should().Contain("Net cash position — CAD");
        cut.Markup.Should().Contain("−CAD 150"); // signed, non-USD format
        cut.Markup.Should().Contain("underwater");
    }
}
