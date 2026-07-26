using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RD.Domain;
using RD.Domain.Entities;
using RD.Infrastructure;
using RD.Infrastructure.Enforcement;
using RD.Infrastructure.Gateways;
using RD.Infrastructure.Sync;
using RD.Tests.Integration.TestInfra;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace RD.Tests.Integration;

/// <summary>
/// The live `close` tag write (final step of Convert→Bill→Close). Enabled → read-before-write then
/// POST `close` and move Paid → Closed; already-present → skip the POST but still close; disabled
/// (default) or kill-switch → no-op and no GHL calls.
/// </summary>
public sealed class ConvertCloseWriteJobTests : IDisposable
{
    private readonly SyncTestDb _db = new();
    private readonly WireMockServer _server = WireMockServer.Start();
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        _server.Stop();
        _server.Dispose();
        _db.Dispose();
    }

    private ConvertCloseWriteJob Job(bool enabled, bool killed = false)
    {
        var ghlOptions = Options.Create(new GhlOptions
        {
            BaseUrl = _server.Urls[0],
            Locations = [new GhlLocationOptions { LocationId = "loc", Token = "pit" }],
        });
        // TestMode off so the write targets the real contact path (the redirect is covered in GhlGatewayTagTests).
        var ghl = new GhlGateway(new HttpClient(), ghlOptions, Options.Create(new SafetyOptions { GhlTestMode = false }),
            new RetryHelper { BaseDelay = TimeSpan.FromMilliseconds(1) });

        var ks = new KillSwitchService(_db.Factory, _clock);
        if (killed) ks.EngageAsync("test", "test", CancellationToken.None).GetAwaiter().GetResult();

        return new ConvertCloseWriteJob(
            _db.Factory, ghl, ks,
            Options.Create(new ConvertOptions { CloseTagWriteEnabled = enabled }),
            Options.Create(new EnforcementOptions { DefaultDunningLocationId = "loc" }),
            _clock, NullLogger<ConvertCloseWriteJob>.Instance);
    }

    private Guid SeedPaidIntent(string? contactId)
    {
        using var db = _db.CreateContext();
        var client = new Client
        {
            Id = Guid.NewGuid(), BusinessName = "Close Co", ContractType = ContractType.Paid,
            AccountType = AccountType.Own, CreatedAt = _clock.UtcNow,
        };
        db.Clients.Add(client);
        var intent = new ConvertIntent
        {
            Id = Guid.NewGuid(), ClientId = client.Id, AccountType = AccountType.Own,
            State = ConvertIntentState.Paid, CloseTagContactId = contactId,
            CreatedAt = _clock.UtcNow, UpdatedAt = _clock.UtcNow,
        };
        db.ConvertIntents.Add(intent);
        db.SaveChanges();
        return intent.Id;
    }

    private void StubTags(string contactId, params string[] tags) =>
        _server.Given(Request.Create().WithPath($"/contacts/{contactId}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBodyAsJson(new { contact = new { id = contactId, tags } }));

    [Fact]
    public async Task Enabled_writes_close_when_absent_and_moves_to_closed()
    {
        var intentId = SeedPaidIntent("ghl_c1");
        StubTags("ghl_c1", "trial");
        _server.Given(Request.Create().WithPath("/contacts/ghl_c1/tags").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        await Job(enabled: true).RunAsync(CancellationToken.None);

        await using var db = _db.CreateContext();
        var intent = db.ConvertIntents.Single(i => i.Id == intentId);
        intent.State.Should().Be(ConvertIntentState.Closed);
        intent.CloseTagWrittenAt.Should().NotBeNull();
        _server.LogEntries.Should().Contain(e => e.RequestMessage.Path == "/contacts/ghl_c1/tags");
    }

    [Fact]
    public async Task Enabled_skips_the_post_when_close_already_present()
    {
        var intentId = SeedPaidIntent("ghl_c1");
        StubTags("ghl_c1", "trial", "close"); // already tagged

        await Job(enabled: true).RunAsync(CancellationToken.None);

        await using var db = _db.CreateContext();
        db.ConvertIntents.Single(i => i.Id == intentId).State.Should().Be(ConvertIntentState.Closed);
        _server.LogEntries.Should().NotContain(e => e.RequestMessage.Path == "/contacts/ghl_c1/tags"); // no POST
    }

    /// <summary>
    /// Gap 2: a paid conversion whose client has NO GHL contact must not vanish. It raises an
    /// investigation (deduped) so the stuck client is visible, instead of sitting in Paid forever.
    /// </summary>
    [Fact]
    public async Task Paid_without_a_ghl_contact_raises_one_investigation_and_stays_paid()
    {
        var intentId = SeedPaidIntent(contactId: null);

        await Job(enabled: true).RunAsync(CancellationToken.None);
        await Job(enabled: true).RunAsync(CancellationToken.None); // second pass must not duplicate

        await using var db = _db.CreateContext();
        db.ConvertIntents.Single(i => i.Id == intentId).State.Should().Be(ConvertIntentState.Paid);
        var items = db.InvestigationItems.Where(i => i.Status == InvestigationStatus.Open).ToList();
        items.Should().ContainSingle();
        items[0].Detail.Should().Contain("no linked GHL contact");
        items[0].System.Should().Be(ExternalSystem.Ghl);
        _server.LogEntries.Should().BeEmpty(); // never called GHL
    }

    /// <summary>
    /// Self-heal: a GHL contact linked AFTER the payment landed is picked up on the next pass —
    /// the conversion completes rather than needing a re-run of the payment.
    /// </summary>
    [Fact]
    public async Task Contact_linked_after_payment_is_picked_up_on_the_next_pass()
    {
        var intentId = SeedPaidIntent(contactId: null);
        Guid clientId;
        using (var db = _db.CreateContext())
        {
            clientId = db.ConvertIntents.Single(i => i.Id == intentId).ClientId;
            db.IdentityLinks.Add(new IdentityLink
            {
                Id = Guid.NewGuid(), ClientId = clientId, System = ExternalSystem.Ghl,
                Kind = LinkKind.Contact, ExternalId = "ghl_late", CreatedAt = _clock.UtcNow,
            });
            db.SaveChanges();
        }
        StubTags("ghl_late", "trial");
        _server.Given(Request.Create().WithPath("/contacts/ghl_late/tags").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        await Job(enabled: true).RunAsync(CancellationToken.None);

        await using var check = _db.CreateContext();
        var intent = check.ConvertIntents.Single(i => i.Id == intentId);
        intent.State.Should().Be(ConvertIntentState.Closed);
        intent.CloseTagContactId.Should().Be("ghl_late");
    }

    [Fact]
    public async Task Disabled_is_a_no_op_and_touches_no_ghl()
    {
        var intentId = SeedPaidIntent("ghl_c1");

        await Job(enabled: false).RunAsync(CancellationToken.None);

        await using var db = _db.CreateContext();
        db.ConvertIntents.Single(i => i.Id == intentId).State.Should().Be(ConvertIntentState.Paid);
        _server.LogEntries.Should().BeEmpty();
    }

    [Fact]
    public async Task KillSwitch_engaged_is_a_no_op()
    {
        var intentId = SeedPaidIntent("ghl_c1");

        await Job(enabled: true, killed: true).RunAsync(CancellationToken.None);

        await using var db = _db.CreateContext();
        db.ConvertIntents.Single(i => i.Id == intentId).State.Should().Be(ConvertIntentState.Paid);
        _server.LogEntries.Should().BeEmpty();
    }
}
