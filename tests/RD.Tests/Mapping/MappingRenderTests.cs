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
        // The panel's Verify/Promote controls are Operator-gated (AuthorizeView) —
        // give the render an authenticated Operator so those controls appear.
        var auth = this.AddAuthorization();
        auth.SetAuthorized("operator@test");
        auth.SetRoles("Operator");
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

    [Fact]
    public void DetailPanel_shows_every_active_link_before_the_operator_verifies()
    {
        var first = new LinkView(
            Guid.NewGuid(), ExternalSystem.Stripe, LinkKind.Customer, "cus_first", 1,
            Verified: true, Invalidated: false, DateTimeOffset.UtcNow,
            "active subscription", ExternalUrl: null);
        var second = new LinkView(
            Guid.NewGuid(), ExternalSystem.Stripe, LinkKind.Customer, "cus_second", 2,
            Verified: false, Invalidated: false, VerifiedAt: null,
            "recent paid invoice", ExternalUrl: null);
        var slots = RequiredLinks.All.Select(s =>
            s.System == ExternalSystem.Stripe && s.Kind == LinkKind.Customer
                ? new RequiredSlot(s.System, s.Kind, s.Label, s.HelpText, first, [second])
                : new RequiredSlot(s.System, s.Kind, s.Label, s.HelpText, Active: null))
            .ToList();
        var detail = new ClientMappingDetail(
            Guid.NewGuid(), "Two-account business", EnforcementMode.Shadow, AccountType.Master, "USD",
            slots, [first, second], [], new BlastRadiusSummary(0, 0m, "USD"),
            HasCurrentVerification: false, LastVerifiedAt: null, LastVerifiedBy: null);

        var cut = Render<MappingDetailPanel>(p => p.Add(x => x.Detail, detail));

        cut.Markup.Should().Contain("cus_first").And.Contain("cus_second").And.Contain("2 linked");
    }
}
