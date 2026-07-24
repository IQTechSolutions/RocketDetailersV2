using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RD.Domain;
using RD.Domain.Entities;
using RD.Infrastructure.Enforcement;
using RD.Infrastructure.Reconciliation;
using RD.Tests.Integration.TestInfra;

namespace RD.Tests.Integration;

/// <summary>
/// Merging a duplicate client into a survivor: links re-parent (so future sync
/// resolves to the survivor), the append-only ledger rolls up into the survivor's
/// balance, blank survivor fields fill from the duplicate, and the duplicate is
/// retired — never deleted, so its live vendor records keep being monitored.
/// </summary>
public sealed class ClientMergeServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 08, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly SyncTestDb _db = new();
    private readonly ClientMergeService _service;

    public ClientMergeServiceTests()
        => _service = new ClientMergeService(_db.Factory, new TestClock(Now));

    private Guid SeedClient(string name, Action<Client>? tweak = null,
        params (ExternalSystem System, LinkKind Kind, string ExternalId)[] links)
    {
        using var db = _db.CreateContext();
        var c = new Client
        {
            Id = Guid.NewGuid(), BusinessName = name, ContractType = ContractType.Paid,
            AccountType = AccountType.Master, CreatedAt = Now,
        };
        tweak?.Invoke(c);
        db.Clients.Add(c);
        foreach (var (sys, kind, ext) in links)
            db.IdentityLinks.Add(new IdentityLink
            {
                Id = Guid.NewGuid(), ClientId = c.Id, System = sys, Kind = kind, ExternalId = ext, CreatedAt = Now,
            });
        db.SaveChanges();
        return c.Id;
    }

    private void AddLedger(Guid clientId, LedgerEntryType type, decimal signed, string objId)
    {
        using var db = _db.CreateContext();
        db.LedgerEntries.Add(new LedgerEntry
        {
            Id = Guid.NewGuid(), ClientId = clientId, OccurredAt = Now, RecordedAt = Now,
            Type = type, SignedAmount = signed, SourceSystem = ExternalSystem.Stripe, SourceObjectId = objId,
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task Merge_reparents_links_retires_the_duplicate_and_rolls_up_its_ledger()
    {
        var survivor = SeedClient("Ace Detailing", links: (ExternalSystem.Stripe, LinkKind.Customer, "cus_survivor"));
        var duplicate = SeedClient("Ace Detailing (dup)",
            links: (ExternalSystem.Stripe, LinkKind.Customer, "cus_dup"));
        AddLedger(survivor, LedgerEntryType.ChargePaid, 1000m, "ch_survivor");
        AddLedger(duplicate, LedgerEntryType.ChargePaid, 500m, "ch_dup");
        AddLedger(duplicate, LedgerEntryType.AdSpend, -800m, "ad_dup");

        var result = await _service.MergeAsync(survivor, duplicate, "operator");

        result.Ok.Should().BeTrue();
        result.SurvivorId.Should().Be(survivor);

        await using var ctx = _db.CreateContext();

        // The duplicate is retired (pointer + timestamp + inert mode), not deleted.
        var dup = await ctx.Clients.FindAsync(duplicate);
        dup.Should().NotBeNull();
        dup!.MergedIntoClientId.Should().Be(survivor);
        dup.MergedAt.Should().Be(Now);
        dup.EnforcementMode.Should().Be(EnforcementMode.Shadow);

        // Its link now resolves to the survivor — future sync attributes here.
        var dupLink = await ctx.IdentityLinks.SingleAsync(l => l.ExternalId == "cus_dup");
        dupLink.ClientId.Should().Be(survivor);

        // The survivor's balance rolls up its own ledger PLUS the retired duplicate's,
        // even though those append-only rows keep the duplicate's ClientId.
        var builder = new ClientStateBuilder(new TestClock(Now), Options.Create(new EnforcementOptions()));
        var context = await builder.LoadContextAsync(ctx, default);
        var survivorClient = await ctx.Clients.FindAsync(survivor);
        var state = await builder.BuildAsync(ctx, survivorClient!, context, default);

        state.TotalPaid.Should().Be(1500m);   // 1000 (own) + 500 (rolled up)
        state.TotalAdSpend.Should().Be(800m);  // 0 (own) + 800 (rolled up)
    }

    [Fact]
    public async Task Survivor_keeps_its_own_fields_and_fills_only_its_blanks_from_the_duplicate()
    {
        var survivor = SeedClient("Ace Detailing", c => { c.Email = null; c.Phone = "111"; c.ContactName = null; });
        var duplicate = SeedClient("Ace Detailing (dup)", c => { c.Email = "dup@example.com"; c.Phone = "999"; c.ContactName = "Dana"; });

        var result = await _service.MergeAsync(survivor, duplicate, "operator");
        result.Ok.Should().BeTrue();

        await using var ctx = _db.CreateContext();
        var s = await ctx.Clients.FindAsync(survivor);
        s!.Email.Should().Be("dup@example.com"); // blank → filled from duplicate
        s.ContactName.Should().Be("Dana");        // blank → filled from duplicate
        s.Phone.Should().Be("111");               // survivor had one → survivor wins
    }

    [Fact]
    public async Task A_client_cannot_be_merged_into_itself()
    {
        var a = SeedClient("Ace Detailing");
        var result = await _service.MergeAsync(a, a, "operator");
        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("itself");
    }

    [Fact]
    public async Task An_already_merged_duplicate_is_blocked_from_merging_again()
    {
        var a = SeedClient("Ace Detailing");
        var b = SeedClient("Ace Detailing (dup)");
        var c = SeedClient("Some Other Co");
        (await _service.MergeAsync(a, b, "operator")).Ok.Should().BeTrue();

        var result = await _service.MergeAsync(c, b, "operator");

        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("already merged");
    }

    public void Dispose() => _db.Dispose();
}
