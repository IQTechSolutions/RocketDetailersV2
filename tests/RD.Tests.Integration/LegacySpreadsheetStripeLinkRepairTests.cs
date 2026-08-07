using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using RD.Domain;
using RD.Domain.Entities;
using RD.Infrastructure.Enforcement;
using RD.Infrastructure.Reconciliation;
using RD.Tests.Integration.TestInfra;

namespace RD.Tests.Integration;

public sealed class LegacySpreadsheetStripeLinkRepairTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 9, 0, 0, TimeSpan.Zero);
    private readonly SyncTestDb _db = new();

    [Fact]
    public async Task Run_adds_secondary_links_from_both_exact_spreadsheet_formats_and_keeps_investigations_open()
    {
        var legacyClientId = Guid.NewGuid();
        var currentClientId = Guid.NewGuid();
        await using (var seed = _db.CreateContext())
        {
            seed.Clients.AddRange(NewClient(legacyClientId, "Legacy"), NewClient(currentClientId, "Current"));
            seed.InvestigationItems.AddRange(
                NewInvestigation(
                    legacyClientId,
                    "Second Stripe identity in spreadsheet: customer='cus_legacy_second', subscription='sub_legacy_second'. Merge or invalidate."),
                NewInvestigation(
                    currentClientId,
                    "Second Stripe identity in spreadsheet: customer='cus_current_second', subscription='sub_current_second'. Confirm the same business here; otherwise leave open for manual mapping correction."));
            await seed.SaveChangesAsync();
        }

        var result = await new LegacySpreadsheetStripeLinkRepair(_db.Factory, new TestClock(Now)).RunAsync();

        result.MatchedInvestigations.Should().Be(2);
        result.LinksAdded.Should().Be(4);
        result.ConflictInvestigationsSkipped.Should().Be(0);
        result.ClientsChanged.Should().Be(2);

        await using var verify = _db.CreateContext();
        var links = await verify.IdentityLinks.AsNoTracking().ToListAsync();
        links.Should().Contain(l => l.ClientId == legacyClientId && l.Kind == LinkKind.Customer && l.ExternalId == "cus_legacy_second" && l.InvalidatedAt == null);
        links.Should().Contain(l => l.ClientId == legacyClientId && l.Kind == LinkKind.Subscription && l.ExternalId == "sub_legacy_second" && l.InvalidatedAt == null);
        links.Should().Contain(l => l.ClientId == currentClientId && l.Kind == LinkKind.Customer && l.ExternalId == "cus_current_second" && l.InvalidatedAt == null);
        links.Should().Contain(l => l.ClientId == currentClientId && l.Kind == LinkKind.Subscription && l.ExternalId == "sub_current_second" && l.InvalidatedAt == null);
        var investigations = await verify.InvestigationItems.AsNoTracking().ToListAsync();
        investigations.Should().OnlyContain(i => i.Status == InvestigationStatus.Open);
        investigations.Single(i => i.ClientId == legacyClientId).Detail.Should().Be(
            "Second Stripe identity in spreadsheet: customer='cus_legacy_second', subscription='sub_legacy_second'. Confirm the same business here; otherwise leave open for manual mapping correction.");
        investigations.Single(i => i.ClientId == currentClientId).Detail.Should().Be(
            "Second Stripe identity in spreadsheet: customer='cus_current_second', subscription='sub_current_second'. Confirm the same business here; otherwise leave open for manual mapping correction.");
    }

    [Fact]
    public async Task Run_is_idempotent_when_the_repair_runs_again_at_startup()
    {
        var clientId = Guid.NewGuid();
        await using (var seed = _db.CreateContext())
        {
            seed.Clients.Add(NewClient(clientId, "Idempotent"));
            seed.InvestigationItems.Add(NewInvestigation(
                clientId,
                "Second Stripe identity in spreadsheet: customer='cus_once', subscription='sub_once'. Merge or invalidate."));
            await seed.SaveChangesAsync();
        }
        var repair = new LegacySpreadsheetStripeLinkRepair(_db.Factory, new TestClock(Now));

        var first = await repair.RunAsync();
        var second = await repair.RunAsync();

        first.LinksAdded.Should().Be(2);
        second.MatchedInvestigations.Should().Be(1);
        second.LinksAdded.Should().Be(0);
        second.ClientsChanged.Should().Be(0);
        second.VerificationsInvalidated.Should().Be(0);
        second.ClientsDemoted.Should().Be(0);
        second.OutboxActionsSuperseded.Should().Be(0);
        await using var verify = _db.CreateContext();
        (await verify.IdentityLinks.CountAsync(l => l.ClientId == clientId
                                                     && l.System == ExternalSystem.Stripe
                                                     && l.InvalidatedAt == null)).Should().Be(2);
        (await verify.InvestigationItems.SingleAsync(i => i.ClientId == clientId)).Status
            .Should().Be(InvestigationStatus.Open);
    }

    [Fact]
    public async Task Run_waits_for_an_in_flight_client_mutation_before_repairing_links()
    {
        var clientId = Guid.NewGuid();
        await using (var seed = _db.CreateContext())
        {
            seed.Clients.Add(NewClient(clientId, "Rolling deployment"));
            seed.InvestigationItems.Add(NewInvestigation(
                clientId,
                "Second Stripe identity in spreadsheet: customer='cus_rolling', subscription='sub_rolling'. Merge or invalidate."));
            await seed.SaveChangesAsync();
        }

        await using var liveInstanceDb = _db.CreateContext();
        var liveInstanceFence = await ClientMutationFence.AcquireAsync(liveInstanceDb, clientId);
        Task<LegacySpreadsheetStripeLinkRepairResult>? repairTask = null;
        try
        {
            repairTask = new LegacySpreadsheetStripeLinkRepair(_db.Factory, new TestClock(Now)).RunAsync();

            var first = await Task.WhenAny(repairTask, Task.Delay(TimeSpan.FromMilliseconds(200)));
            first.Should().NotBe(
                repairTask,
                "phase-two startup repair must wait for a phase-one instance already mutating this client");

            await using var check = _db.CreateContext();
            (await check.IdentityLinks.AnyAsync(link => link.ClientId == clientId)).Should().BeFalse();
        }
        finally
        {
            await liveInstanceFence.DisposeAsync();
        }

        var result = await repairTask!;
        result.LinksAdded.Should().Be(2);
    }

    [Fact]
    public async Task Run_repairs_a_preexisting_resolved_legacy_item_without_reopening_history()
    {
        var clientId = Guid.NewGuid();
        var investigation = NewInvestigation(
            clientId,
            "Second Stripe identity in spreadsheet: customer='cus_resolved_legacy', subscription='sub_resolved_legacy'. Merge or invalidate.");
        investigation.Status = InvestigationStatus.Resolved;
        investigation.ResolvedAt = Now.AddDays(-10);
        investigation.ResolvedBy = "legacy-operator";
        investigation.ResolutionNote = "Resolved before the hidden Stripe identities were attached.";

        await using (var seed = _db.CreateContext())
        {
            seed.Clients.Add(NewClient(clientId, "Resolved legacy row"));
            seed.InvestigationItems.Add(investigation);
            await seed.SaveChangesAsync();
        }

        var result = await new LegacySpreadsheetStripeLinkRepair(_db.Factory, new TestClock(Now)).RunAsync();

        result.MatchedInvestigations.Should().Be(1);
        result.LinksAdded.Should().Be(2);
        result.ClientsChanged.Should().Be(1);

        await using var verify = _db.CreateContext();
        var historical = await verify.InvestigationItems.AsNoTracking()
            .SingleAsync(item => item.Id == investigation.Id);
        historical.Status.Should().Be(InvestigationStatus.Resolved);
        historical.ResolvedAt.Should().Be(Now.AddDays(-10));
        historical.ResolvedBy.Should().Be("legacy-operator");
        historical.ResolutionNote.Should().Be(
            "Resolved before the hidden Stripe identities were attached.");
        historical.Detail.Should().EndWith(". Merge or invalidate.");

        var openBlockers = await verify.InvestigationItems.AsNoTracking()
            .Where(item => item.ClientId == clientId
                           && item.Kind == InvestigationKind.DuplicateStripeCustomer
                           && item.Status == InvestigationStatus.Open)
            .ToListAsync();
        openBlockers.Should().ContainSingle();
        openBlockers[0].Id.Should().NotBe(investigation.Id);
        openBlockers[0].Detail.Should().Be(
            "Second Stripe identity in spreadsheet: customer='cus_resolved_legacy', subscription='sub_resolved_legacy'. Confirm the same business here; otherwise leave open for manual mapping correction.");
        (await verify.IdentityLinks.AsNoTracking()
                .Where(link => link.ClientId == clientId && link.System == ExternalSystem.Stripe)
                .ToListAsync())
            .Should().Contain(link => link.Kind == LinkKind.Customer
                                      && link.ExternalId == "cus_resolved_legacy"
                                      && link.InvalidatedAt == null)
            .And.Contain(link => link.Kind == LinkKind.Subscription
                                 && link.ExternalId == "sub_resolved_legacy"
                                 && link.InvalidatedAt == null);
    }

    [Fact]
    public async Task Run_repairs_on_the_active_survivor_when_the_legacy_client_merges_during_phase_one()
    {
        var duplicateId = Guid.NewGuid();
        var survivorId = Guid.NewGuid();
        var investigation = NewInvestigation(
            duplicateId,
            "Second Stripe identity in spreadsheet: customer='cus_phase_one_merge', subscription='sub_phase_one_merge'. Merge or invalidate.");
        await using (var seed = _db.CreateContext())
        {
            seed.Clients.AddRange(
                NewClient(duplicateId, "Merged while waiting"),
                NewClient(survivorId, "Active survivor"));
            seed.InvestigationItems.Add(investigation);
            await seed.SaveChangesAsync();
        }

        await using var mergeDb = _db.CreateContext();
        var mergeFences = await ClientMutationFence.AcquireManyAsync(
            mergeDb,
            new[] { duplicateId, survivorId });
        Task<LegacySpreadsheetStripeLinkRepairResult>? repairTask = null;
        try
        {
            repairTask = new LegacySpreadsheetStripeLinkRepair(
                _db.Factory,
                new TestClock(Now)).RunAsync();

            var first = await Task.WhenAny(repairTask, Task.Delay(TimeSpan.FromMilliseconds(200)));
            first.Should().NotBe(
                repairTask,
                "phase one must wait for the client merge holding its mutation fence");

            var duplicate = await mergeDb.Clients.SingleAsync(client => client.Id == duplicateId);
            duplicate.MergedIntoClientId = survivorId;
            duplicate.MergedAt = Now.AddMinutes(-1);
            duplicate.EnforcementMode = EnforcementMode.Shadow;

            var mergedItem = await mergeDb.InvestigationItems
                .SingleAsync(item => item.Id == investigation.Id);
            mergedItem.Status = InvestigationStatus.Resolved;
            mergedItem.ResolvedAt = Now.AddMinutes(-1);
            mergedItem.ResolvedBy = "merge-operator";
            mergedItem.ResolutionNote = "Resolved by merge into the active survivor.";
            await mergeDb.SaveChangesAsync();
        }
        finally
        {
            await mergeFences.DisposeAsync();
        }

        var result = await repairTask!;

        result.MatchedInvestigations.Should().Be(1);
        result.LinksAdded.Should().Be(2);
        result.ClientsChanged.Should().Be(1);

        await using var verify = _db.CreateContext();
        (await verify.IdentityLinks.AsNoTracking()
                .Where(link => link.System == ExternalSystem.Stripe
                               && (link.ExternalId == "cus_phase_one_merge"
                                   || link.ExternalId == "sub_phase_one_merge"))
                .ToListAsync())
            .Should().HaveCount(2)
            .And.OnlyContain(link => link.ClientId == survivorId && link.InvalidatedAt == null);

        var historical = await verify.InvestigationItems.AsNoTracking()
            .SingleAsync(item => item.Id == investigation.Id);
        historical.ClientId.Should().Be(duplicateId);
        historical.Status.Should().Be(InvestigationStatus.Resolved);
        historical.ResolvedBy.Should().Be("merge-operator");
        historical.ResolutionNote.Should().Be("Resolved by merge into the active survivor.");

        var survivorBlockers = await verify.InvestigationItems.AsNoTracking()
            .Where(item => item.ClientId == survivorId
                           && item.Kind == InvestigationKind.DuplicateStripeCustomer
                           && item.Status == InvestigationStatus.Open)
            .ToListAsync();
        survivorBlockers.Should().ContainSingle();
        survivorBlockers[0].Id.Should().NotBe(investigation.Id);
        survivorBlockers[0].Detail.Should().Contain("cus_phase_one_merge");
    }

    [Fact]
    public async Task Run_skips_an_investigation_when_the_secondary_ids_are_active_on_another_client()
    {
        var targetClientId = Guid.NewGuid();
        var ownerClientId = Guid.NewGuid();
        var investigation = NewInvestigation(
            targetClientId,
            "Second Stripe identity in spreadsheet: customer='cus_owned_elsewhere', subscription='sub_owned_elsewhere'. Merge or invalidate.");
        await using (var seed = _db.CreateContext())
        {
            seed.Clients.AddRange(NewClient(targetClientId, "Target"), NewClient(ownerClientId, "Owner"));
            seed.IdentityLinks.AddRange(
                NewLink(ownerClientId, LinkKind.Customer, "cus_owned_elsewhere"),
                NewLink(ownerClientId, LinkKind.Subscription, "sub_owned_elsewhere"));
            seed.InvestigationItems.Add(investigation);
            await seed.SaveChangesAsync();
        }

        var result = await new LegacySpreadsheetStripeLinkRepair(_db.Factory, new TestClock(Now)).RunAsync();

        result.MatchedInvestigations.Should().Be(1);
        result.ConflictInvestigationsSkipped.Should().Be(1);
        result.LinksAdded.Should().Be(0);
        result.ClientsChanged.Should().Be(0);
        await using var verify = _db.CreateContext();
        (await verify.IdentityLinks.AnyAsync(l => l.ClientId == targetClientId
                                                  && l.System == ExternalSystem.Stripe)).Should().BeFalse();
        (await verify.IdentityLinks.CountAsync(l => l.ClientId == ownerClientId
                                                     && l.System == ExternalSystem.Stripe
                                                     && l.InvalidatedAt == null)).Should().Be(2);
        var openInvestigation = await verify.InvestigationItems.SingleAsync(i => i.Id == investigation.Id);
        openInvestigation.Status.Should().Be(InvestigationStatus.Open);
        openInvestigation.Detail.Should().Be(
            "Second Stripe identity in spreadsheet: customer='cus_owned_elsewhere', subscription='sub_owned_elsewhere'. Confirm the same business here; otherwise leave open for manual mapping correction.");
    }

    [Fact]
    public async Task Run_creates_a_survivor_blocker_when_a_resolved_legacy_item_conflicts()
    {
        var targetClientId = Guid.NewGuid();
        var ownerClientId = Guid.NewGuid();
        var investigation = NewInvestigation(
            targetClientId,
            "Second Stripe identity in spreadsheet: customer='cus_resolved_conflict', subscription='sub_resolved_conflict'. Merge or invalidate.");
        investigation.Status = InvestigationStatus.Resolved;
        investigation.ResolvedAt = Now.AddDays(-5);
        investigation.ResolvedBy = "legacy-operator";
        investigation.ResolutionNote = "Incorrectly closed before ownership was checked.";
        await using (var seed = _db.CreateContext())
        {
            seed.Clients.AddRange(NewClient(targetClientId, "Conflict target"), NewClient(ownerClientId, "Conflict owner"));
            seed.IdentityLinks.Add(NewLink(ownerClientId, LinkKind.Customer, "cus_resolved_conflict"));
            seed.InvestigationItems.Add(investigation);
            await seed.SaveChangesAsync();
        }

        var result = await new LegacySpreadsheetStripeLinkRepair(_db.Factory, new TestClock(Now)).RunAsync();

        result.MatchedInvestigations.Should().Be(1);
        result.ConflictInvestigationsSkipped.Should().Be(1);
        result.LinksAdded.Should().Be(0);
        await using var verify = _db.CreateContext();
        (await verify.InvestigationItems.AsNoTracking()
                .Where(item => item.ClientId == targetClientId
                               && item.Kind == InvestigationKind.DuplicateStripeCustomer
                               && item.Status == InvestigationStatus.Open)
                .ToListAsync())
            .Should().ContainSingle()
            .Which.Detail.Should().Contain("cus_resolved_conflict");
        (await verify.InvestigationItems.SingleAsync(item => item.Id == investigation.Id)).Status
            .Should().Be(InvestigationStatus.Resolved);
        (await verify.IdentityLinks.AnyAsync(link => link.ClientId == targetClientId
                                                     && link.System == ExternalSystem.Stripe))
            .Should().BeFalse();
    }

    [Fact]
    public async Task Run_skips_the_whole_pair_when_an_id_exists_in_invalidated_history()
    {
        var clientId = Guid.NewGuid();
        var historicalLink = NewLink(clientId, LinkKind.Customer, "cus_invalidated_history");
        historicalLink.InvalidatedAt = Now.AddDays(-10);
        await using (var seed = _db.CreateContext())
        {
            seed.Clients.Add(NewClient(clientId, "Historical owner"));
            seed.IdentityLinks.Add(historicalLink);
            seed.InvestigationItems.Add(NewInvestigation(
                clientId,
                "Second Stripe identity in spreadsheet: customer='cus_invalidated_history', subscription='sub_must_not_be_partially_added'. Merge or invalidate."));
            await seed.SaveChangesAsync();
        }

        var result = await new LegacySpreadsheetStripeLinkRepair(_db.Factory, new TestClock(Now)).RunAsync();

        result.MatchedInvestigations.Should().Be(1);
        result.ConflictInvestigationsSkipped.Should().Be(1);
        result.LinksAdded.Should().Be(0);
        await using var verify = _db.CreateContext();
        (await verify.IdentityLinks.AnyAsync(link =>
                link.ExternalId == "sub_must_not_be_partially_added"))
            .Should().BeFalse();
    }

    [Fact]
    public async Task Run_skips_both_clients_when_the_batch_proposes_the_same_Stripe_key_for_each()
    {
        var firstClientId = Guid.NewGuid();
        var secondClientId = Guid.NewGuid();
        await using (var seed = _db.CreateContext())
        {
            seed.Clients.AddRange(
                NewClient(firstClientId, "First proposal"),
                NewClient(secondClientId, "Second proposal"));
            seed.InvestigationItems.AddRange(
                NewInvestigation(
                    firstClientId,
                    "Second Stripe identity in spreadsheet: customer='cus_batch_conflict', subscription='sub_first'. Merge or invalidate."),
                NewInvestigation(
                    secondClientId,
                    "Second Stripe identity in spreadsheet: customer='cus_batch_conflict', subscription='sub_second'. Merge or invalidate."));
            await seed.SaveChangesAsync();
        }

        var result = await new LegacySpreadsheetStripeLinkRepair(_db.Factory, new TestClock(Now)).RunAsync();

        result.MatchedInvestigations.Should().Be(2);
        result.ConflictInvestigationsSkipped.Should().Be(2);
        result.LinksAdded.Should().Be(0);
        result.ClientsChanged.Should().Be(0);
        await using var verify = _db.CreateContext();
        (await verify.IdentityLinks.AnyAsync(link =>
                link.ClientId == firstClientId || link.ClientId == secondClientId))
            .Should().BeFalse();
    }

    [Fact]
    public async Task Run_invalidates_current_verifications_and_demotes_a_changed_client_to_shadow()
    {
        var clientId = Guid.NewGuid();
        var client = NewClient(clientId, "Previously automatic");
        client.EnforcementMode = EnforcementMode.Auto;
        await using (var seed = _db.CreateContext())
        {
            seed.Clients.Add(client);
            seed.MappingVerifications.AddRange(
                NewVerification(clientId, Now.AddDays(-2)),
                NewVerification(clientId, Now.AddDays(-1)));
            seed.InvestigationItems.Add(NewInvestigation(
                clientId,
                "Second Stripe identity in spreadsheet: customer='cus_demote', subscription='sub_demote'. Merge or invalidate."));
            await seed.SaveChangesAsync();
        }

        var result = await new LegacySpreadsheetStripeLinkRepair(_db.Factory, new TestClock(Now)).RunAsync();

        result.LinksAdded.Should().Be(2);
        result.ClientsChanged.Should().Be(1);
        result.VerificationsInvalidated.Should().Be(2);
        result.ClientsDemoted.Should().Be(1);
        await using var verify = _db.CreateContext();
        (await verify.Clients.SingleAsync(c => c.Id == clientId)).EnforcementMode
            .Should().Be(EnforcementMode.Shadow);
        (await verify.MappingVerifications.AsNoTracking()
                .Where(v => v.ClientId == clientId)
                .ToListAsync())
            .Should().OnlyContain(v => v.InvalidatedAt == Now);
        (await verify.IdentityLinks.AsNoTracking()
                .Where(l => l.ClientId == clientId && l.System == ExternalSystem.Stripe)
                .ToListAsync())
            .Should().OnlyContain(l => l.VerifiedAt == null);
    }

    [Fact]
    public async Task Run_supersedes_only_nonterminal_actions_for_clients_whose_identity_set_changed()
    {
        var changedClientId = Guid.NewGuid();
        var unchangedClientId = Guid.NewGuid();
        var terminalAction = NewAction(changedClientId, OutboxStatus.Executed, "terminal");
        var unaffectedAction = NewAction(unchangedClientId, OutboxStatus.Approved, "unaffected");
        var affectedActions = new[]
        {
            NewAction(changedClientId, OutboxStatus.Pending, "pending"),
            NewAction(changedClientId, OutboxStatus.AwaitingApproval, "awaiting"),
            NewAction(changedClientId, OutboxStatus.Approved, "approved"),
            NewAction(changedClientId, OutboxStatus.Leased, "leased"),
            NewAction(changedClientId, OutboxStatus.Failed, "failed"),
        };
        affectedActions[3].LeaseOwner = "old-worker";
        affectedActions[3].FencingToken = 42;
        affectedActions[3].LeaseUntil = Now.AddMinutes(5);
        affectedActions[3].NextAttemptAt = Now.AddMinutes(10);

        await using (var seed = _db.CreateContext())
        {
            seed.Clients.AddRange(
                NewClient(changedClientId, "Mapping changed"),
                NewClient(unchangedClientId, "Unchanged"));
            seed.InvestigationItems.Add(NewInvestigation(
                changedClientId,
                "Second Stripe identity in spreadsheet: customer='cus_action_guard', subscription='sub_action_guard'. Merge or invalidate."));
            seed.OutboxActions.AddRange(affectedActions);
            seed.OutboxActions.AddRange(terminalAction, unaffectedAction);
            await seed.SaveChangesAsync();
        }

        var result = await new LegacySpreadsheetStripeLinkRepair(_db.Factory, new TestClock(Now)).RunAsync();

        result.OutboxActionsSuperseded.Should().Be(affectedActions.Length);
        await using var verify = _db.CreateContext();
        var repaired = await verify.OutboxActions.AsNoTracking()
            .Where(action => affectedActions.Select(expected => expected.Id).Contains(action.Id))
            .ToListAsync();
        repaired.Should().OnlyContain(action =>
            action.Status == OutboxStatus.Superseded
            && action.ActionVersion == 2
            && action.LeaseOwner == null
            && action.FencingToken == null
            && action.LeaseUntil == null
            && action.NextAttemptAt == null
            && action.LastError != null);
        (await verify.OutboxActions.SingleAsync(action => action.Id == terminalAction.Id)).Status
            .Should().Be(OutboxStatus.Executed);
        (await verify.OutboxActions.SingleAsync(action => action.Id == unaffectedAction.Id)).Status
            .Should().Be(OutboxStatus.Approved);
    }

    private static Client NewClient(Guid id, string name) => new()
    {
        Id = id,
        BusinessName = name,
        ContractType = ContractType.Paid,
        AccountType = AccountType.Master,
        EnforcementMode = EnforcementMode.Shadow,
        CreatedAt = Now.AddDays(-30),
    };

    private static InvestigationItem NewInvestigation(Guid clientId, string detail) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        Kind = InvestigationKind.DuplicateStripeCustomer,
        System = ExternalSystem.Stripe,
        Status = InvestigationStatus.Open,
        Detail = detail,
        CreatedAt = Now.AddDays(-20),
    };

    private static IdentityLink NewLink(Guid clientId, LinkKind kind, string externalId) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        System = ExternalSystem.Stripe,
        Kind = kind,
        ExternalId = externalId,
        CreatedAt = Now.AddDays(-30),
    };

    private static MappingVerification NewVerification(Guid clientId, DateTimeOffset verifiedAt) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        VerifiedLinksJson = "[]",
        VerifiedBy = "legacy-reviewer",
        BlastRadiusAcknowledged = true,
        VerifiedAt = verifiedAt,
    };

    private static OutboxAction NewAction(Guid clientId, OutboxStatus status, string suffix) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        DecisionId = Guid.Empty,
        ActionType = OutboxActionType.PauseCampaign,
        PayloadJson = "{}",
        IdempotencyKey = $"legacy-repair:{suffix}:{Guid.NewGuid():N}",
        Status = status,
        ExpectedKillSwitchEpoch = 0,
        CreatedAt = Now.AddDays(-1),
        ExecutedAt = status == OutboxStatus.Executed ? Now.AddHours(-1) : null,
    };

    public void Dispose() => _db.Dispose();
}
