using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RD.Domain;
using RD.Domain.Entities;
using RD.Infrastructure.Sync;
using RD.Tests.Integration.TestInfra;

namespace RD.Tests.Integration;

/// <summary>
/// The expiry sweep reaps conversions billed but never paid. Only AwaitingPayment intents whose
/// ExpiresAt has passed move to Expired; fresh ones, no-expiry ones, and already-Paid ones are left
/// alone (the WHERE-guarded set update is the race guard against the first-payment webhook).
/// </summary>
public sealed class ConvertExpirySweepJobTests : IDisposable
{
    private readonly SyncTestDb _db = new();
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _db.Dispose();

    private ConvertExpirySweepJob Job() => new(_db.Factory, _clock, NullLogger<ConvertExpirySweepJob>.Instance);

    private Guid SeedIntent(ConvertIntentState state, DateTimeOffset? expiresAt)
    {
        using var db = _db.CreateContext();
        var client = new Client
        {
            Id = Guid.NewGuid(), BusinessName = "Sweep Co", ContractType = ContractType.Trial,
            AccountType = AccountType.Own, CreatedAt = _clock.UtcNow,
        };
        db.Clients.Add(client);
        var intent = new ConvertIntent
        {
            Id = Guid.NewGuid(), ClientId = client.Id, AccountType = AccountType.Own,
            State = state, ExpiresAt = expiresAt, CreatedAt = _clock.UtcNow, UpdatedAt = _clock.UtcNow,
        };
        db.ConvertIntents.Add(intent);
        db.SaveChanges();
        return intent.Id;
    }

    [Fact]
    public async Task Sweep_expires_only_awaiting_payment_past_its_window()
    {
        var stale = SeedIntent(ConvertIntentState.AwaitingPayment, _clock.UtcNow.AddDays(-1));
        var fresh = SeedIntent(ConvertIntentState.AwaitingPayment, _clock.UtcNow.AddDays(1));
        var paid = SeedIntent(ConvertIntentState.Paid, _clock.UtcNow.AddDays(-1));

        await Job().RunAsync(CancellationToken.None);

        await using var db = _db.CreateContext();
        db.ConvertIntents.Single(i => i.Id == stale).State.Should().Be(ConvertIntentState.Expired);
        db.ConvertIntents.Single(i => i.Id == fresh).State.Should().Be(ConvertIntentState.AwaitingPayment);
        db.ConvertIntents.Single(i => i.Id == paid).State.Should().Be(ConvertIntentState.Paid);
    }

    [Fact]
    public async Task Sweep_ignores_awaiting_payment_with_no_expiry()
    {
        var noExpiry = SeedIntent(ConvertIntentState.AwaitingPayment, expiresAt: null);

        await Job().RunAsync(CancellationToken.None);

        await using var db = _db.CreateContext();
        db.ConvertIntents.Single(i => i.Id == noExpiry).State.Should().Be(ConvertIntentState.AwaitingPayment);
    }
}
