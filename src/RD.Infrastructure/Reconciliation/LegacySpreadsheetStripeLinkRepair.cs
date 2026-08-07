using Microsoft.EntityFrameworkCore;
using RD.Domain;
using RD.Domain.Entities;
using RD.Infrastructure.Enforcement;

namespace RD.Infrastructure.Reconciliation;

/// <summary>
/// Backfills the secondary Stripe identities that the legacy spreadsheet
/// importer recorded in an investigation but did not attach as identity links.
/// Investigation ownership remains open for an operator to confirm.
/// </summary>
public sealed class LegacySpreadsheetStripeLinkRepair(
    IDbContextFactory<RdDbContext> factory,
    IClock clock)
{
    private const string DetailPrefix =
        "Second Stripe identity in spreadsheet: customer='";
    private const string DetailSeparator =
        "', subscription='";
    private const string LegacyDetailSuffix =
        "'. Merge or invalidate.";
    private const string CurrentDetailSuffix =
        "'. Confirm the same business here; otherwise leave open for manual mapping correction.";

    public async Task<LegacySpreadsheetStripeLinkRepairResult> RunAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = clock.UtcNow;
        var candidates = await db.InvestigationItems
            .Where(i => i.Kind == InvestigationKind.DuplicateStripeCustomer
                        && (i.Status == InvestigationStatus.Open
                            || i.Status == InvestigationStatus.Resolved)
                        && i.ClientId != null
                        && i.System == ExternalSystem.Stripe)
            .OrderBy(i => i.CreatedAt)
            .ThenBy(i => i.Id)
            .ToListAsync(ct);
        var parsed = ParseCandidates(candidates);
        var stablePlan = await AcquireStablePlanAsync(db, candidates, parsed, ct);
        await using var mutationFences = stablePlan.MutationFences;
        await using var ownershipFence =
            await ClientMutationFence.AcquireMappingOwnershipAsync(db, ct);
        parsed = stablePlan.Parsed.ToList();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // A previous app instance can still be draining during a rolling deploy.
        // AcquireStablePlanAsync re-read after the fences and followed any client
        // retired by a merge to its active survivor. Resolved legacy rows remain
        // immutable audit history; their replacement ownership blocker is a new row.
        var candidateClientIds = parsed
            .Where(item => item.TargetClientId is not null)
            .Select(item => item.TargetClientId!.Value)
            .Distinct()
            .ToList();

        var clients = await db.Clients
            .Where(client => candidateClientIds.Contains(client.Id)
                             && client.MergedIntoClientId == null)
            .ToDictionaryAsync(client => client.Id, ct);
        var knownLinks = await db.IdentityLinks
            .Where(l => l.System == ExternalSystem.Stripe
                        && (l.Kind == LinkKind.Customer || l.Kind == LinkKind.Subscription))
            .ToListAsync(ct);

        // Preflight the complete batch. If the spreadsheet proposed the same
        // vendor key for different clients, neither client wins based on row order.
        var crossClientKeys = parsed
            .SelectMany(item => item.DesiredLinks.Select(link => (
                ClientId: item.TargetClientId ?? item.SourceClientId,
                Link: link)))
            .GroupBy(proposal => proposal.Link, StripeLinkComparer.Instance)
            .Where(group => group.Select(proposal => proposal.ClientId).Distinct().Skip(1).Any())
            .Select(group => group.Key)
            .ToHashSet(StripeLinkComparer.Instance);

        var openBlockerClientIds = (await db.InvestigationItems
                .Where(item => item.ClientId != null
                               && candidateClientIds.Contains(item.ClientId.Value)
                               && item.Kind == InvestigationKind.DuplicateStripeCustomer
                               && item.Status == InvestigationStatus.Open)
                .Select(item => item.ClientId!.Value)
                .Distinct()
                .ToListAsync(ct))
            .ToHashSet();

        var linksAdded = 0;
        var conflictInvestigations = 0;
        var changedClients = new HashSet<Guid>();

        foreach (var item in parsed)
        {
            var currentDetail = CurrentDetail(item.Investigation.Detail);
            if (item.Investigation.Status == InvestigationStatus.Open
                && item.TargetClientId == item.SourceClientId
                && !string.Equals(item.Investigation.Detail, currentDetail, StringComparison.Ordinal))
            {
                item.Investigation.Detail = currentDetail;
            }

            if (item.TargetClientId is Guid targetClientId
                && clients.ContainsKey(targetClientId)
                && openBlockerClientIds.Add(targetClientId))
            {
                db.InvestigationItems.Add(new InvestigationItem
                {
                    Id = Guid.NewGuid(),
                    ClientId = targetClientId,
                    Kind = InvestigationKind.DuplicateStripeCustomer,
                    System = ExternalSystem.Stripe,
                    ExternalId = item.DesiredLinks
                        .OrderBy(link => link.Kind == LinkKind.Customer ? 0 : 1)
                        .Select(link => link.ExternalId)
                        .FirstOrDefault(),
                    Status = InvestigationStatus.Open,
                    Detail = currentDetail,
                    CreatedAt = now,
                });
            }

            // The database's vendor-key index includes invalidated history. Treat
            // any key occupied anywhere other than an active link on this client
            // as a conflict: never steal/rewrite historical ownership at startup,
            // and never partially attach a customer/subscription pair.
            if (item.TargetClientId is not Guid activeClientId)
            {
                conflictInvestigations++;
                continue;
            }

            var hasConflict = !clients.ContainsKey(activeClientId)
                              || item.DesiredLinks.Any(crossClientKeys.Contains)
                              || item.DesiredLinks.Any(desired => knownLinks
                                  .Where(link => StripeLinkComparer.Instance.Equals(
                                      desired, new StripeLink(link.Kind, link.ExternalId)))
                                  .Any(link => link.ClientId != activeClientId || link.InvalidatedAt != null));
            if (hasConflict)
            {
                conflictInvestigations++;
                continue;
            }

            foreach (var desired in item.DesiredLinks)
            {
                var exists = knownLinks.Any(link =>
                    link.ClientId == activeClientId
                    && link.InvalidatedAt == null
                    && StripeLinkComparer.Instance.Equals(
                        desired, new StripeLink(link.Kind, link.ExternalId)));
                if (exists) continue;

                var link = new IdentityLink
                {
                    Id = Guid.NewGuid(),
                    ClientId = activeClientId,
                    System = ExternalSystem.Stripe,
                    Kind = desired.Kind,
                    ExternalId = desired.ExternalId,
                    LinkVersion = knownLinks
                        .Where(existing => existing.ClientId == activeClientId
                                           && existing.Kind == desired.Kind)
                        .Select(existing => existing.LinkVersion)
                        .DefaultIfEmpty(0)
                        .Max() + 1,
                    VerifiedAt = null,
                    CreatedAt = now,
                };
                db.IdentityLinks.Add(link);
                knownLinks.Add(link);
                linksAdded++;
                changedClients.Add(activeClientId);
            }
        }

        var changedClientIds = changedClients.ToList();
        var currentVerifications = await db.MappingVerifications
            .Where(verification => changedClientIds.Contains(verification.ClientId)
                                   && verification.InvalidatedAt == null)
            .ToListAsync(ct);
        foreach (var verification in currentVerifications)
            verification.InvalidatedAt = now;

        var clientsDemoted = 0;
        foreach (var clientId in changedClients)
        {
            var client = clients[clientId];
            if (client.EnforcementMode == EnforcementMode.Shadow) continue;
            client.EnforcementMode = EnforcementMode.Shadow;
            clientsDemoted++;
        }

        // A mapping-verification reset must also retire work that was staged
        // against the old identity set. This repair runs before Hangfire starts,
        // so even a lease left behind by a previous process can be closed safely.
        // Terminal audit rows are preserved unchanged.
        var queuedActions = await db.OutboxActions
            .Where(action => changedClientIds.Contains(action.ClientId)
                             && (action.Status == OutboxStatus.Pending
                                 || action.Status == OutboxStatus.AwaitingApproval
                                 || action.Status == OutboxStatus.Approved
                                 || action.Status == OutboxStatus.Leased
                                 || action.Status == OutboxStatus.Failed))
            .ToListAsync(ct);
        foreach (var action in queuedActions)
        {
            action.Status = OutboxStatus.Superseded;
            action.ActionVersion++;
            action.LeaseOwner = null;
            action.FencingToken = null;
            action.LeaseUntil = null;
            action.NextAttemptAt = null;
            action.LastError = "Superseded because legacy Stripe identity repair changed the client's mapping; review and verify the complete mapping before staging a new action.";
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new LegacySpreadsheetStripeLinkRepairResult(
            parsed.Count,
            changedClients.Count,
            linksAdded,
            conflictInvestigations,
            currentVerifications.Count,
            clientsDemoted,
            queuedActions.Count);
    }

    private static async Task<StableRepairPlan> AcquireStablePlanAsync(
        RdDbContext db,
        IReadOnlyList<InvestigationItem> candidates,
        IReadOnlyList<ParsedInvestigation> initialParsed,
        CancellationToken ct)
    {
        var parsed = initialParsed.ToList();
        var initiallyResolvedIds = parsed
            .Where(item => item.Investigation.Status == InvestigationStatus.Resolved)
            .Select(item => item.Investigation.Id)
            .ToHashSet();
        var fenceIds = parsed.Select(item => item.SourceClientId).ToHashSet();

        while (true)
        {
            // Most already-merged rows acquire source + survivor in one ordered
            // pass. The post-lock check below handles a merge that completes
            // between this topology read and acquisition of the source fence.
            var preliminaryTopology = await LoadClientTopologyAsync(db, ct);
            foreach (var item in parsed)
            {
                var (_, path) = ResolveActiveClient(item.SourceClientId, preliminaryTopology);
                fenceIds.UnionWith(path);
            }

            var fences = await ClientMutationFence.AcquireManyAsync(db, fenceIds, ct);
            var keepFences = false;
            try
            {
                foreach (var candidate in candidates)
                    await db.Entry(candidate).ReloadAsync(ct);

                parsed = ParseCandidates(candidates);
                var lockedTopology = await LoadClientTopologyAsync(db, ct);
                var requiredFenceIds = new HashSet<Guid>();
                var resolved = new List<ParsedInvestigation>(parsed.Count);
                foreach (var item in parsed)
                {
                    var (activeClientId, path) = ResolveActiveClient(
                        item.SourceClientId,
                        lockedTopology);
                    requiredFenceIds.UnionWith(path);

                    // A normal operator resolution that completed while startup
                    // waited stays resolved and is not replayed. The exceptions
                    // are rows already resolved when phase one began and rows a
                    // concurrent merge resolved onto a different active owner.
                    if (item.Investigation.Status == InvestigationStatus.Resolved
                        && !initiallyResolvedIds.Contains(item.Investigation.Id)
                        && activeClientId == item.SourceClientId)
                    {
                        continue;
                    }

                    resolved.Add(item with { TargetClientId = activeClientId });
                }

                if (requiredFenceIds.All(fenceIds.Contains))
                {
                    keepFences = true;
                    return new StableRepairPlan(fences, resolved);
                }

                // A merge exposed a survivor whose lock was not in the phase-one
                // snapshot. Release everything and reacquire the expanded set in
                // global Guid order rather than extending locks out of order.
                fenceIds.UnionWith(requiredFenceIds);
            }
            finally
            {
                if (!keepFences)
                    await fences.DisposeAsync();
            }
        }
    }

    private static async Task<IReadOnlyDictionary<Guid, ClientTopology>> LoadClientTopologyAsync(
        RdDbContext db,
        CancellationToken ct) =>
        await db.Clients.AsNoTracking()
            .Select(client => new ClientTopology(client.Id, client.MergedIntoClientId))
            .ToDictionaryAsync(client => client.Id, ct);

    private static (Guid? ActiveClientId, IReadOnlyCollection<Guid> Path) ResolveActiveClient(
        Guid sourceClientId,
        IReadOnlyDictionary<Guid, ClientTopology> topology)
    {
        var path = new List<Guid>();
        var visited = new HashSet<Guid>();
        var currentId = sourceClientId;
        while (visited.Add(currentId))
        {
            path.Add(currentId);
            if (!topology.TryGetValue(currentId, out var current))
                return (null, path);
            if (current.MergedIntoClientId is not Guid survivorId)
                return (currentId, path);
            currentId = survivorId;
        }

        // Defensive handling for corrupt merge cycles: lock every observed row,
        // attach nothing, and count the candidate as a conflict.
        return (null, path);
    }

    private static List<ParsedInvestigation> ParseCandidates(
        IEnumerable<InvestigationItem> candidates) =>
        candidates
            .Where(IsRepairCandidate)
            .Select(item => TryParseDetail(item.Detail, out var customerId, out var subscriptionId)
                ? new ParsedInvestigation(
                    item,
                    item.ClientId!.Value,
                    TargetClientId: null,
                    DesiredLinks(customerId, subscriptionId).ToList())
                : null)
            .Where(item => item is not null)
            .Select(item => item!)
            .ToList();

    private static bool IsRepairCandidate(InvestigationItem item) =>
        item.Kind == InvestigationKind.DuplicateStripeCustomer
        && item.ClientId is not null
        && item.System == ExternalSystem.Stripe
        && (item.Status == InvestigationStatus.Resolved
            || (item.Status == InvestigationStatus.Open
                && item.ResolvedAt is null
                && item.ResolvedBy is null
                && item.ResolutionNote is null));

    private static string CurrentDetail(string detail) =>
        detail.EndsWith(LegacyDetailSuffix, StringComparison.Ordinal)
            ? detail[..^LegacyDetailSuffix.Length] + CurrentDetailSuffix
            : detail;

    private static IEnumerable<StripeLink> DesiredLinks(
        string? customerId,
        string? subscriptionId)
    {
        if (!string.IsNullOrWhiteSpace(customerId))
            yield return new StripeLink(LinkKind.Customer, customerId);
        if (!string.IsNullOrWhiteSpace(subscriptionId))
            yield return new StripeLink(LinkKind.Subscription, subscriptionId);
    }

    private static bool TryParseDetail(
        string detail,
        out string? customerId,
        out string? subscriptionId)
    {
        customerId = null;
        subscriptionId = null;
        if (!detail.StartsWith(DetailPrefix, StringComparison.Ordinal)) return false;

        var suffix = detail.EndsWith(LegacyDetailSuffix, StringComparison.Ordinal)
            ? LegacyDetailSuffix
            : detail.EndsWith(CurrentDetailSuffix, StringComparison.Ordinal)
                ? CurrentDetailSuffix
                : null;
        if (suffix is null) return false;

        var payload = detail[DetailPrefix.Length..^suffix.Length];
        var separatorAt = payload.IndexOf(DetailSeparator, StringComparison.Ordinal);
        if (separatorAt < 0
            || payload.IndexOf(DetailSeparator, separatorAt + DetailSeparator.Length, StringComparison.Ordinal) >= 0)
            return false;

        customerId = payload[..separatorAt];
        subscriptionId = payload[(separatorAt + DetailSeparator.Length)..];
        if (customerId.Length == 0 && subscriptionId.Length == 0) return false;
        return (customerId.Length == 0 || IsValidStripeId(customerId, "cus_"))
               && (subscriptionId.Length == 0 || IsValidStripeId(subscriptionId, "sub_"));
    }

    private static bool IsValidStripeId(string value, string prefix) =>
        value.Length is > 4 and <= 100
        && value.StartsWith(prefix, StringComparison.Ordinal)
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    private sealed record ParsedInvestigation(
        InvestigationItem Investigation,
        Guid SourceClientId,
        Guid? TargetClientId,
        IReadOnlyList<StripeLink> DesiredLinks);

    private sealed record StableRepairPlan(
        IAsyncDisposable MutationFences,
        IReadOnlyList<ParsedInvestigation> Parsed);

    private sealed record ClientTopology(Guid Id, Guid? MergedIntoClientId);

    private readonly record struct StripeLink(LinkKind Kind, string ExternalId);

    private sealed class StripeLinkComparer : IEqualityComparer<StripeLink>
    {
        public static readonly StripeLinkComparer Instance = new();

        public bool Equals(StripeLink x, StripeLink y) =>
            x.Kind == y.Kind
            && string.Equals(x.ExternalId, y.ExternalId, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(StripeLink obj) =>
            HashCode.Combine(obj.Kind, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ExternalId));
    }
}

public sealed record LegacySpreadsheetStripeLinkRepairResult(
    int MatchedInvestigations,
    int ClientsChanged,
    int LinksAdded,
    int ConflictInvestigationsSkipped,
    int VerificationsInvalidated,
    int ClientsDemoted,
    int OutboxActionsSuperseded);
