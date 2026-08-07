using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using RD.Domain;
using RD.Domain.Entities;
using RD.Infrastructure.Reconciliation;
using RD.Tests.Integration.TestInfra;

namespace RD.Tests.Integration;

public sealed class LegacyInvestigationCleanupTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 7, 20, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CreatedAt = Now.AddDays(-14);
    private readonly SyncTestDb _db = new();

    [Fact]
    public async Task Run_dismisses_only_the_exact_open_legacy_item_and_preserves_its_original_fields()
    {
        var targetClientId = Guid.NewGuid();
        var target = NewItem(targetClientId, LegacyInvestigationCleanup.LegacyDetail);
        var genuinePolicyItem = NewItem(Guid.NewGuid(),
            "Client is paid up but a campaign was paused outside the app — never auto-resume someone else's pause.");
        var alreadyClosed = NewItem(Guid.NewGuid(), LegacyInvestigationCleanup.LegacyDetail);
        alreadyClosed.Status = InvestigationStatus.Resolved;
        alreadyClosed.ResolvedAt = Now.AddDays(-1);
        alreadyClosed.ResolvedBy = "operator";
        alreadyClosed.ResolutionNote = "Reviewed manually.";
        var nearMiss = NewItem(Guid.NewGuid(),
            "At least one Stripe customer here is delinquent (unpaid invoices). ");
        var caseOnlyNearMiss = NewItem(Guid.NewGuid(),
            "at least one Stripe customer here is delinquent (unpaid invoice).");
        var trailingSpaceNearMiss = NewItem(Guid.NewGuid(),
            LegacyInvestigationCleanup.LegacyDetail + " ");
        var itemWithVendorMetadata = NewItem(Guid.NewGuid(), LegacyInvestigationCleanup.LegacyDetail);
        itemWithVendorMetadata.System = ExternalSystem.Stripe;
        itemWithVendorMetadata.ExternalId = "cus_already_classified";

        await using (var seed = _db.CreateContext())
        {
            seed.InvestigationItems.AddRange(
                target, genuinePolicyItem, alreadyClosed, nearMiss, caseOnlyNearMiss,
                trailingSpaceNearMiss, itemWithVendorMetadata);
            await seed.SaveChangesAsync();
        }

        var cleanup = new LegacyInvestigationCleanup(_db.Factory, new TestClock(Now));
        var affected = await cleanup.RunAsync();

        affected.Should().Be(1);
        await using var verify = _db.CreateContext();
        var cleaned = await verify.InvestigationItems.SingleAsync(i => i.Id == target.Id);
        cleaned.Status.Should().Be(InvestigationStatus.Dismissed);
        cleaned.ResolvedAt.Should().Be(Now);
        cleaned.ResolvedBy.Should().Be(LegacyInvestigationCleanup.SystemActor);
        cleaned.ResolutionNote.Should().Be(LegacyInvestigationCleanup.AuditNote);
        cleaned.ClientId.Should().Be(targetClientId);
        cleaned.Kind.Should().Be(InvestigationKind.ExternallyPausedPayment);
        cleaned.Detail.Should().Be(LegacyInvestigationCleanup.LegacyDetail);
        cleaned.CreatedAt.Should().Be(CreatedAt);
        cleaned.System.Should().BeNull();
        cleaned.ExternalId.Should().BeNull();

        var untouchedIds = new[]
        {
            genuinePolicyItem.Id, alreadyClosed.Id, nearMiss.Id, caseOnlyNearMiss.Id,
            trailingSpaceNearMiss.Id, itemWithVendorMetadata.Id,
        };
        var untouched = await verify.InvestigationItems
            .Where(i => untouchedIds.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id);

        untouched[genuinePolicyItem.Id].Status.Should().Be(InvestigationStatus.Open);
        untouched[genuinePolicyItem.Id].ResolvedAt.Should().BeNull();
        untouched[alreadyClosed.Id].Status.Should().Be(InvestigationStatus.Resolved);
        untouched[alreadyClosed.Id].ResolvedAt.Should().Be(Now.AddDays(-1));
        untouched[alreadyClosed.Id].ResolutionNote.Should().Be("Reviewed manually.");
        untouched[nearMiss.Id].Status.Should().Be(InvestigationStatus.Open);
        untouched[caseOnlyNearMiss.Id].Status.Should().Be(InvestigationStatus.Open);
        untouched[trailingSpaceNearMiss.Id].Status.Should().Be(InvestigationStatus.Open);
        untouched[itemWithVendorMetadata.Id].Status.Should().Be(InvestigationStatus.Open);
    }

    [Fact]
    public async Task Run_is_idempotent_and_a_second_run_returns_zero()
    {
        var target = NewItem(Guid.NewGuid(), LegacyInvestigationCleanup.LegacyDetail);
        await using (var seed = _db.CreateContext())
        {
            seed.InvestigationItems.Add(target);
            await seed.SaveChangesAsync();
        }

        var cleanup = new LegacyInvestigationCleanup(_db.Factory, new TestClock(Now));

        (await cleanup.RunAsync()).Should().Be(1);
        (await cleanup.RunAsync()).Should().Be(0);

        await using var verify = _db.CreateContext();
        var cleaned = await verify.InvestigationItems.SingleAsync(i => i.Id == target.Id);
        cleaned.Status.Should().Be(InvestigationStatus.Dismissed);
        cleaned.ResolvedAt.Should().Be(Now);
        cleaned.ResolvedBy.Should().Be(LegacyInvestigationCleanup.SystemActor);
        cleaned.ResolutionNote.Should().Be(LegacyInvestigationCleanup.AuditNote);
    }

    private static InvestigationItem NewItem(Guid clientId, string detail) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        Kind = InvestigationKind.ExternallyPausedPayment,
        Detail = detail,
        Status = InvestigationStatus.Open,
        CreatedAt = CreatedAt,
    };

    public void Dispose() => _db.Dispose();
}
