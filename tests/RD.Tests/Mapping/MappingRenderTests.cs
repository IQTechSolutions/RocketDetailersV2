using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using RD.Domain;
using RD.Web.Components.Mapping;
using RD.Web.Services;

namespace RD.Tests.Mapping;

/// <summary>
/// bUnit render tests for the wizard's empty state and its blast-radius alert.
/// Both panels render from passed-in parameters (no DB), exactly like the
/// cockpit's F6-state child components.
/// </summary>
public class MappingRenderTests : BunitContext, IAsyncLifetime
{
    public MappingRenderTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor registers IAsyncDisposable-only services — tear down asynchronously.
    async Task IAsyncLifetime.DisposeAsync() => await DisposeAsync();

    [Fact]
    public void ClientList_shows_the_all_mapped_empty_state_when_nothing_needs_mapping()
    {
        var cut = Render<MappingClientList>(p => p.Add(x => x.Rows, new List<UnverifiedClientRow>()));

        cut.Markup.Should().Contain("Every master-account client is mapped and verified");
    }

    [Fact]
    public void DetailPanel_renders_the_blast_radius_alert_from_the_detail()
    {
        var campaigns = new List<CampaignFact>
        {
            new("c1", "Acme Detailing — Spring", "ACTIVE", DailyBudget: null, RecentDailySpend: 25m, Currency: "USD"),
            new("c2", "Acme Detailing — Summer", "ACTIVE", DailyBudget: null, RecentDailySpend: 10m, Currency: "USD"),
        };
        var blastRadius = BlastRadius.Compute(campaigns, "USD");
        var slots = RequiredLinks.All
            .Select(s => new RequiredSlot(s.System, s.Kind, s.Label, s.HelpText, Active: null))
            .ToList();

        var detail = new ClientMappingDetail(
            Guid.NewGuid(), "Acme Detailing", EnforcementMode.Shadow, AccountType.Master, "USD",
            slots, new List<LinkView>(), campaigns, blastRadius,
            HasCurrentVerification: false, LastVerifiedAt: null, LastVerifiedBy: null);

        var cut = Render<MappingDetailPanel>(p => p.Add(x => x.Detail, detail));

        cut.Markup.Should().Contain("Acme Detailing");
        cut.Markup.Should().Contain("2 campaigns");
        cut.Markup.Should().Contain("$35");
        cut.Markup.Should().Contain("/day for this client");
        // The required-acknowledgement gate is present, disabled until links + box.
        cut.Markup.Should().Contain("I've verified these links control the right client's ads");
    }
}
