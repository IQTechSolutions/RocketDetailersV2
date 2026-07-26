using FluentAssertions;
using RD.Domain;
using RD.Domain.Entities;
using RD.Tests.Integration.TestInfra;
using RD.Web.Services;

namespace RD.Tests.Integration;

/// <summary>
/// The conversions queue (Gap 3): every conversion is listed, and Attention flags exactly the ones a
/// human must act on — expired-unpaid, and paid-but-onboarding-can't-fire. Healthy/terminal rows are
/// listed with no flag so the queue stays honest rather than noisy.
/// </summary>
public sealed class ConversionsQueueTests : IDisposable
{
    private readonly SyncTestDb _db = new();
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _db.Dispose();

    private ConvertService Service() => new(_db.Factory, _clock);

    private Guid Seed(string name, ConvertIntentState state, bool withGhlContact,
        DateTimeOffset? expiresAt = null, DateTimeOffset? closeWrittenAt = null)
    {
        using var db = _db.CreateContext();
        var client = new Client
        {
            Id = Guid.NewGuid(), BusinessName = name, ContractType = ContractType.Trial,
            AccountType = AccountType.Own, CreatedAt = _clock.UtcNow,
        };
        db.Clients.Add(client);
        if (withGhlContact)
            db.IdentityLinks.Add(new IdentityLink
            {
                Id = Guid.NewGuid(), ClientId = client.Id, System = ExternalSystem.Ghl,
                Kind = LinkKind.Contact, ExternalId = "ghl_" + name, CreatedAt = _clock.UtcNow,
            });
        var intent = new ConvertIntent
        {
            Id = Guid.NewGuid(), ClientId = client.Id, AccountType = AccountType.Own, State = state,
            ExpiresAt = expiresAt, CloseTagWrittenAt = closeWrittenAt,
            CreatedAt = _clock.UtcNow, UpdatedAt = _clock.UtcNow,
        };
        db.ConvertIntents.Add(intent);
        db.SaveChanges();
        return intent.Id;
    }

    [Fact]
    public async Task Lists_every_conversion_with_its_client_name()
    {
        Seed("Alpha Detailing", ConvertIntentState.Closed, withGhlContact: true, closeWrittenAt: _clock.UtcNow);
        Seed("Beta Detailing", ConvertIntentState.AwaitingPayment, withGhlContact: true, expiresAt: _clock.UtcNow.AddDays(5));

        var rows = await Service().ListConversionsAsync();

        rows.Should().HaveCount(2);
        rows.Select(r => r.BusinessName).Should().Contain(["Alpha Detailing", "Beta Detailing"]);
    }

    [Fact]
    public async Task Flags_a_paid_conversion_with_no_ghl_contact()
    {
        var id = Seed("No Contact Co", ConvertIntentState.Paid, withGhlContact: false);

        var row = (await Service().ListConversionsAsync()).Single(r => r.IntentId == id);

        row.HasGhlContact.Should().BeFalse();
        row.Attention.Should().Contain("no GHL contact");
    }

    [Fact]
    public async Task Flags_an_expired_unpaid_conversion()
    {
        var id = Seed("Never Paid Co", ConvertIntentState.Expired, withGhlContact: true);

        var row = (await Service().ListConversionsAsync()).Single(r => r.IntentId == id);

        row.Attention.Should().Contain("never paid");
    }

    [Fact]
    public async Task Flags_paid_awaiting_the_close_tag_but_not_once_written()
    {
        var waiting = Seed("Awaiting Tag Co", ConvertIntentState.Paid, withGhlContact: true);
        var done = Seed("Done Co", ConvertIntentState.Closed, withGhlContact: true, closeWrittenAt: _clock.UtcNow);

        var rows = await Service().ListConversionsAsync();

        rows.Single(r => r.IntentId == waiting).Attention.Should().Contain("close");
        rows.Single(r => r.IntentId == done).Attention.Should().BeNull(); // healthy, no noise
    }
}
