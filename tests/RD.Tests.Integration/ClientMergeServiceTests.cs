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

    [Fact]
    public async Task Unmerge_restores_the_duplicate_moves_its_links_back_and_un_rolls_up_the_ledger()
    {
        var survivor = SeedClient("Ace Detailing", links: (ExternalSystem.Stripe, LinkKind.Customer, "cus_survivor"));
        var duplicate = SeedClient("Ace Detailing (dup)",
            c => c.EnforcementMode = EnforcementMode.Auto, // a real prior mode to restore
            (ExternalSystem.Stripe, LinkKind.Customer, "cus_dup"));
        AddLedger(survivor, LedgerEntryType.ChargePaid, 1000m, "ch_survivor");
        AddLedger(duplicate, LedgerEntryType.ChargePaid, 500m, "ch_dup");

        (await _service.MergeAsync(survivor, duplicate, "operator")).Ok.Should().BeTrue();
        var undo = await _service.UnmergeAsync(duplicate, "operator");

        undo.Ok.Should().BeTrue();
        undo.RestoredClientId.Should().Be(duplicate);

        await using var ctx = _db.CreateContext();

        // The duplicate is live again: pointer cleared, timestamp cleared, prior mode restored.
        var dup = await ctx.Clients.FindAsync(duplicate);
        dup!.MergedIntoClientId.Should().BeNull();
        dup.MergedAt.Should().BeNull();
        dup.EnforcementMode.Should().Be(EnforcementMode.Auto);

        // Its link resolves back to the duplicate.
        var dupLink = await ctx.IdentityLinks.SingleAsync(l => l.ExternalId == "cus_dup");
        dupLink.ClientId.Should().Be(duplicate);

        // The rolled-up ledger returns: survivor is back to its own, duplicate owns its own.
        var builder = new ClientStateBuilder(new TestClock(Now), Options.Create(new EnforcementOptions()));
        var context = await builder.LoadContextAsync(ctx, default);
        var survivorState = await builder.BuildAsync(ctx, (await ctx.Clients.FindAsync(survivor))!, context, default);
        var dupState = await builder.BuildAsync(ctx, dup, context, default);
        survivorState.TotalPaid.Should().Be(1000m);
        dupState.TotalPaid.Should().Be(500m);

        // The audit is retained but marked reversed.
        var audit = await ctx.ClientMergeAudits.SingleAsync(a => a.DuplicateId == duplicate);
        audit.ReversedAt.Should().Be(Now);
        audit.ReversedBy.Should().Be("operator");
    }

    [Fact]
    public async Task Unmerge_clears_only_the_blanks_the_merge_filled_and_leaves_survivor_own_fields()
    {
        var survivor = SeedClient("Ace Detailing", c => { c.Email = null; c.Phone = "111"; });
        var duplicate = SeedClient("Ace Detailing (dup)", c => { c.Email = "dup@example.com"; c.Phone = "999"; });

        (await _service.MergeAsync(survivor, duplicate, "operator")).Ok.Should().BeTrue();
        (await _service.UnmergeAsync(duplicate, "operator")).Ok.Should().BeTrue();

        await using var ctx = _db.CreateContext();
        var s = await ctx.Clients.FindAsync(survivor);
        s!.Email.Should().BeNull();  // was blank, filled by merge, cleared by unmerge
        s.Phone.Should().Be("111");   // survivor's own — untouched throughout
    }

    [Fact]
    public async Task Unmerge_does_not_clobber_a_field_a_human_edited_after_the_merge()
    {
        var survivor = SeedClient("Ace Detailing", c => c.Email = null);
        var duplicate = SeedClient("Ace Detailing (dup)", c => c.Email = "dup@example.com");

        (await _service.MergeAsync(survivor, duplicate, "operator")).Ok.Should().BeTrue();

        // A human curates the survivor's email after the merge.
        using (var edit = _db.CreateContext())
        {
            var s = await edit.Clients.FindAsync(survivor);
            s!.Email = "curated@example.com";
            await edit.SaveChangesAsync();
        }

        (await _service.UnmergeAsync(duplicate, "operator")).Ok.Should().BeTrue();

        await using var ctx = _db.CreateContext();
        var survivorAfter = await ctx.Clients.FindAsync(survivor);
        survivorAfter!.Email.Should().Be("curated@example.com"); // human edit preserved, not nulled
    }

    [Fact]
    public async Task Unmerge_is_blocked_when_the_account_is_not_merged()
    {
        var a = SeedClient("Ace Detailing");
        var result = await _service.UnmergeAsync(a, "operator");
        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("isn't merged");
    }

    [Fact]
    public async Task Unmerge_is_blocked_for_a_legacy_merge_with_no_snapshot()
    {
        var survivor = SeedClient("Ace Detailing");
        // Simulate a merge performed before unmerge existed: retired pointer, but no audit row.
        var duplicate = SeedClient("Ace Detailing (dup)", c => c.MergedIntoClientId = survivor);

        var result = await _service.UnmergeAsync(duplicate, "operator");

        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("manually");
    }

    public void Dispose() => _db.Dispose();
}
