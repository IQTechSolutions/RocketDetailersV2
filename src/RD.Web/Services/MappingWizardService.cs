using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RD.Domain;
using RD.Domain.Entities;
using RD.Infrastructure;

namespace RD.Web.Services;

/// <summary>
/// The mapping-fix wizard's data + write service. Query THROUGH the context
/// factory (Blazor circuits are long-lived and concurrent — never inject
/// RdDbContext directly). Every mutation is transactional and touches only
/// IdentityLink / MappingVerification / Client.EnforcementMode (and resolves
/// duplicate-Stripe InvestigationItems). A required-link change invalidates the
/// client's current verification and demotes it to Shadow, atomically.
///
/// Host registration (Program.cs, added during integration — NOT in this commit):
///   builder.Services.AddScoped&lt;MappingWizardService&gt;();
/// </summary>
public class MappingWizardService(IDbContextFactory<RdDbContext> factory, IClock clock)
{
    /// <summary>Verifier stamp until Identity (real user names) lands — matches ReconciliationService.</summary>
    private const string DefaultActor = "operator";

    // ---------------- Left rail: who needs mapping ----------------

    /// <summary>
    /// Master-account clients whose required links are incomplete OR who have no
    /// current MappingVerification. Prioritized by open Unmapped / ImportConflict
    /// / DuplicateStripe investigations, then by incompleteness, then by name.
    /// </summary>
    public async Task<List<UnverifiedClientRow>> GetUnverifiedClients(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var clients = await db.Clients.AsNoTracking()
            .Where(c => c.AccountType == AccountType.Master)
            .Select(c => new
            {
                c.Id,
                c.BusinessName,
                c.EnforcementMode,
                HasCustomer = c.IdentityLinks.Any(l => l.System == ExternalSystem.Stripe && l.Kind == LinkKind.Customer && l.InvalidatedAt == null),
                HasSubscription = c.IdentityLinks.Any(l => l.System == ExternalSystem.Stripe && l.Kind == LinkKind.Subscription && l.InvalidatedAt == null),
                HasCampaign = c.IdentityLinks.Any(l => l.System == ExternalSystem.Meta && l.Kind == LinkKind.Campaign && l.InvalidatedAt == null),
                HasContact = c.IdentityLinks.Any(l => l.System == ExternalSystem.Ghl && l.Kind == LinkKind.Contact && l.InvalidatedAt == null),
            })
            .ToListAsync(ct);

        var verifiedClientIds = (await db.MappingVerifications.AsNoTracking()
                .Where(v => v.InvalidatedAt == null)
                .Select(v => v.ClientId)
                .Distinct()
                .ToListAsync(ct))
            .ToHashSet();

        var priorityKinds = new[]
        {
            InvestigationKind.UnmappedIdentity,
            InvestigationKind.ImportConflict,
            InvestigationKind.DuplicateStripeCustomer,
        };
        var priorityByClient = (await db.InvestigationItems.AsNoTracking()
                .Where(i => i.Status == InvestigationStatus.Open && i.ClientId != null && priorityKinds.Contains(i.Kind))
                .GroupBy(i => i.ClientId!.Value)
                .Select(g => new { ClientId = g.Key, Count = g.Count() })
                .ToListAsync(ct))
            .ToDictionary(x => x.ClientId, x => x.Count);

        return clients
            .Select(c => new UnverifiedClientRow(
                c.Id, c.BusinessName, c.EnforcementMode,
                c.HasCustomer, c.HasSubscription, c.HasCampaign, c.HasContact,
                HasCurrentVerification: verifiedClientIds.Contains(c.Id),
                OpenPriorityInvestigations: priorityByClient.GetValueOrDefault(c.Id)))
            // Needs attention: missing a required link, or never verified.
            .Where(r => !r.RequiredLinksComplete || !r.HasCurrentVerification)
            .OrderByDescending(r => r.OpenPriorityInvestigations > 0)
            .ThenByDescending(r => r.OpenPriorityInvestigations)
            .ThenBy(r => r.RequiredLinksComplete) // incomplete (false) first
            .ThenBy(r => r.BusinessName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ---------------- Right rail: one client's mapping as a control ----------------

    public async Task<ClientMappingDetail?> GetClientMappingDetail(Guid clientId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var client = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId, ct);
        if (client is null) return null;

        var links = await db.IdentityLinks.AsNoTracking()
            .Where(l => l.ClientId == clientId)
            .OrderBy(l => l.System).ThenBy(l => l.Kind).ThenByDescending(l => l.CreatedAt)
            .ToListAsync(ct);

        var active = links.Where(l => l.InvalidatedAt == null).ToList();

        // ---- Resolve projection facts for the active links (evidence lines).
        var subIds = active.Where(l => l.System == ExternalSystem.Stripe && l.Kind == LinkKind.Subscription).Select(l => l.ExternalId).ToList();
        var customerIds = active.Where(l => l.System == ExternalSystem.Stripe && l.Kind == LinkKind.Customer).Select(l => l.ExternalId).ToList();
        var campaignIds = active.Where(l => l.System == ExternalSystem.Meta && l.Kind == LinkKind.Campaign).Select(l => l.ExternalId).ToList();
        var contactIds = active.Where(l => l.System == ExternalSystem.Ghl && l.Kind == LinkKind.Contact).Select(l => l.ExternalId).ToList();

        var subs = await db.StripeSubscriptions.AsNoTracking()
            .Where(s => subIds.Contains(s.SubscriptionId) || customerIds.Contains(s.CustomerId))
            .ToListAsync(ct);

        var campaigns = await db.MetaCampaigns.AsNoTracking()
            .Where(m => campaignIds.Contains(m.CampaignId))
            .ToListAsync(ct);

        // Last-2-days spend per campaign (design doc: last-2-days spend from insights).
        var since = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime.Date).AddDays(-2);
        var insights = await db.MetaInsightsDaily.AsNoTracking()
            .Where(i => campaignIds.Contains(i.CampaignId) && i.Date >= since)
            .ToListAsync(ct);
        var spendByCampaign = insights
            .GroupBy(i => i.CampaignId)
            .ToDictionary(
                g => g.Key,
                g => g.Sum(i => i.Spend) / Math.Max(1, g.Select(i => i.Date).Distinct().Count())); // avg daily over observed days

        var lastMessageByContact = (await db.GhlMessages.AsNoTracking()
                .Where(m => contactIds.Contains(m.ContactId))
                .GroupBy(m => m.ContactId)
                .Select(g => new { ContactId = g.Key, LastSentAt = g.Max(m => m.SentAt), Count = g.Count() })
                .ToListAsync(ct))
            .ToDictionary(x => x.ContactId, x => (x.LastSentAt, x.Count));

        var now = clock.UtcNow;

        var campaignFacts = campaigns
            .Select(m => new CampaignFact(
                m.CampaignId, m.Name, m.EffectiveStatus, m.DailyBudget,
                spendByCampaign.GetValueOrDefault(m.CampaignId, 0m),
                client.CurrencyCode))
            .ToList();

        var linkViews = links.Select(l => new LinkView(
            l.Id, l.System, l.Kind, l.ExternalId, l.LinkVersion,
            Verified: l.VerifiedAt is not null,
            Invalidated: l.InvalidatedAt is not null,
            l.VerifiedAt,
            Evidence: ResolveEvidence(l, subs, campaignFacts, lastMessageByContact, now, client.CurrencyCode)))
            .ToList();

        var requiredSlots = RequiredLinks.All.Select(spec =>
        {
            var slotLink = linkViews.FirstOrDefault(v =>
                v.System == spec.System && v.Kind == spec.Kind && !v.Invalidated);
            return new RequiredSlot(spec.System, spec.Kind, spec.Label, spec.HelpText, slotLink);
        }).ToList();

        var blastRadius = BlastRadius.Compute(campaignFacts, client.CurrencyCode);

        var currentVerification = await db.MappingVerifications.AsNoTracking()
            .Where(v => v.ClientId == clientId && v.InvalidatedAt == null)
            .OrderByDescending(v => v.VerifiedAt)
            .FirstOrDefaultAsync(ct);

        return new ClientMappingDetail(
            client.Id, client.BusinessName, client.EnforcementMode, client.AccountType, client.CurrencyCode,
            requiredSlots, linkViews, campaignFacts, blastRadius,
            HasCurrentVerification: currentVerification is not null,
            LastVerifiedAt: currentVerification?.VerifiedAt,
            LastVerifiedBy: currentVerification?.VerifiedBy);
    }

    private static string ResolveEvidence(
        IdentityLink link,
        List<StripeSubscriptionProj> subs,
        List<CampaignFact> campaignFacts,
        Dictionary<string, (DateTimeOffset LastSentAt, int Count)> lastMessageByContact,
        DateTimeOffset now,
        string currency)
    {
        switch (link.System, link.Kind)
        {
            case (ExternalSystem.Stripe, LinkKind.Subscription):
            {
                var sub = subs.FirstOrDefault(s => s.SubscriptionId == link.ExternalId);
                if (sub is null) return "No subscription facts synced yet — evidence appears after the next Stripe sync.";
                var amount = sub.Amount is null ? "amount unknown" : Human.MoneyKpi(sub.Amount.Value, sub.CurrencyCode);
                var interval = string.IsNullOrEmpty(sub.PriceInterval) ? "" : $"/{sub.PriceInterval}";
                return $"{sub.Status} · {amount}{interval}";
            }
            case (ExternalSystem.Stripe, LinkKind.Customer):
            {
                var sub = subs.FirstOrDefault(s => s.CustomerId == link.ExternalId);
                return sub is null
                    ? "Stripe customer — no subscription facts synced yet."
                    : $"Customer of a {sub.Status} subscription.";
            }
            case (ExternalSystem.Meta, LinkKind.Campaign):
            {
                var fact = campaignFacts.FirstOrDefault(c => c.CampaignId == link.ExternalId);
                if (fact is null) return "No campaign facts synced yet — evidence appears after the next Meta sync.";
                var budget = fact.DailyBudget is null ? "no budget set" : $"budget {Human.MoneyKpi(fact.DailyBudget.Value, currency)}/day";
                return $"{fact.DisplayName} · {fact.EffectiveStatus} · {budget} · spend {Human.MoneyKpi(fact.RecentDailySpend, currency)}/day";
            }
            case (ExternalSystem.Ghl, LinkKind.Contact):
            {
                if (!lastMessageByContact.TryGetValue(link.ExternalId, out var msg))
                    return "GHL contact — no messages observed yet.";
                return $"{msg.Count} message(s) observed · last {Human.Ago(msg.LastSentAt, now)}.";
            }
            default:
                return "Linked.";
        }
    }

    // ---------------- Suggestions for a missing link ----------------

    /// <summary>
    /// Fuzzy-suggest candidate external ids for a missing link kind, drawn from
    /// projections not yet actively linked to any client. Best-effort and clearly
    /// labeled as suggestions — verification is what makes them real.
    /// </summary>
    public async Task<IReadOnlyList<LinkSuggestion>> SuggestLinks(
        Guid clientId, ExternalSystem system, LinkKind kind, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var client = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId, ct);
        if (client is null) return [];

        // External ids already claimed by an ACTIVE link of this system+kind (across all clients).
        var claimed = (await db.IdentityLinks.AsNoTracking()
                .Where(l => l.System == system && l.Kind == kind && l.InvalidatedAt == null)
                .Select(l => l.ExternalId)
                .ToListAsync(ct))
            .ToHashSet();

        List<SuggestionCandidate> candidates = (system, kind) switch
        {
            (ExternalSystem.Stripe, LinkKind.Subscription) => (await db.StripeSubscriptions.AsNoTracking()
                    .OrderByDescending(s => s.SourceSyncedAt).Take(200).ToListAsync(ct))
                .Where(s => !claimed.Contains(s.SubscriptionId))
                .Select(s => new SuggestionCandidate(
                    s.SubscriptionId,
                    s.SubscriptionId,
                    s.SourceSyncedAt,
                    $"{s.Status} · {(s.Amount is null ? "amount unknown" : Human.MoneyKpi(s.Amount.Value, s.CurrencyCode))}"))
                .ToList(),

            (ExternalSystem.Stripe, LinkKind.Customer) => (await db.StripeSubscriptions.AsNoTracking()
                    .OrderByDescending(s => s.SourceSyncedAt).Take(200).ToListAsync(ct))
                .Where(s => !claimed.Contains(s.CustomerId))
                .GroupBy(s => s.CustomerId)
                .Select(g => new SuggestionCandidate(
                    g.Key,
                    g.Key,
                    g.Max(s => s.SourceSyncedAt),
                    $"{g.Count()} subscription(s)"))
                .ToList(),

            (ExternalSystem.Meta, LinkKind.Campaign) => (await db.MetaCampaigns.AsNoTracking()
                    .OrderByDescending(m => m.SourceSyncedAt).Take(200).ToListAsync(ct))
                .Where(m => !claimed.Contains(m.CampaignId))
                .Select(m => new SuggestionCandidate(
                    m.CampaignId,
                    m.Name ?? m.CampaignId,
                    m.SourceSyncedAt,
                    m.EffectiveStatus))
                .ToList(),

            (ExternalSystem.Ghl, LinkKind.Contact) => (await db.GhlMessages.AsNoTracking()
                    .OrderByDescending(m => m.SentAt).Take(200).ToListAsync(ct))
                .Where(m => !claimed.Contains(m.ContactId))
                .GroupBy(m => m.ContactId)
                .Select(g => new SuggestionCandidate(
                    g.Key,
                    g.Key,
                    g.Max(m => m.SentAt),
                    $"{g.Count()} message(s) observed"))
                .ToList(),

            _ => [],
        };

        return LinkSuggester.Rank(client.BusinessName, candidates);
    }

    // ---------------- Writes ----------------

    /// <summary>
    /// Create or replace the client's link of a given system+kind. For the
    /// single-cardinality kinds (Customer/Subscription/Contact) the prior active
    /// link is invalidated first; LinkVersion is bumped past the whole history.
    /// A required-link change invalidates the current verification and demotes to
    /// Shadow — all in one transaction.
    /// </summary>
    public async Task<MappingWriteResult> AddOrReplaceLink(
        Guid clientId, ExternalSystem system, LinkKind kind, string externalId, string verifiedBy = DefaultActor, CancellationToken ct = default)
    {
        externalId = externalId?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(externalId))
            return MappingWriteResult.Fail("Enter an external id to link.");

        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = clock.UtcNow;

        var client = await db.Clients.FirstOrDefaultAsync(c => c.Id == clientId, ct);
        if (client is null)
        {
            await tx.RollbackAsync(ct);
            return MappingWriteResult.Fail("Client not found.");
        }

        var existingActive = await db.IdentityLinks
            .Where(l => l.ClientId == clientId && l.System == system && l.Kind == kind && l.InvalidatedAt == null)
            .ToListAsync(ct);

        // Idempotent: this exact link is already active.
        if (existingActive.Any(l => l.ExternalId == externalId))
        {
            await tx.RollbackAsync(ct);
            return MappingWriteResult.Success($"{kind.Label()} is already linked to {externalId}. No change.");
        }

        var singleCardinality = kind is LinkKind.Customer or LinkKind.Subscription or LinkKind.Contact;
        var replaced = singleCardinality && existingActive.Count > 0;

        if (singleCardinality && existingActive.Count > 0)
        {
            // Invalidate the prior active link FIRST (and save) so the filtered
            // unique index (one active Customer/Subscription/Contact per client)
            // never sees two active rows at once.
            foreach (var l in existingActive) l.InvalidatedAt = now;
            await db.SaveChangesAsync(ct);
        }

        var maxVersion = await db.IdentityLinks
            .Where(l => l.ClientId == clientId && l.System == system && l.Kind == kind)
            .Select(l => (int?)l.LinkVersion)
            .MaxAsync(ct) ?? 0;

        db.IdentityLinks.Add(new IdentityLink
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            System = system,
            Kind = kind,
            ExternalId = externalId,
            LinkVersion = maxVersion + 1,
            VerifiedAt = null,      // a new/replaced link is unverified until VerifyMapping locks it in
            InvalidatedAt = null,
            CreatedAt = now,
        });

        // Any required-link change demotes and invalidates the current verification.
        if (RequiredLinks.IsRequired(system, kind))
            await InvalidateVerificationAndDemote(db, client, now, ct);

        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            await tx.RollbackAsync(ct);
            return MappingWriteResult.Fail(
                $"Couldn't link {externalId} — it may already belong to another client. ({ex.GetBaseException().Message})");
        }

        return MappingWriteResult.Success(replaced
            ? $"Replaced the {kind.Label().ToLowerInvariant()} link with {externalId}. Prior link invalidated — re-verify to lock it in."
            : $"Linked {kind.Label().ToLowerInvariant()} {externalId}. Re-verify to lock it in.");
    }

    /// <summary>
    /// Verify the mapping: requires all four required links present + the blast
    /// radius acknowledged. Writes a MappingVerification pinning the verified link
    /// ids+versions (JSON), stamps VerifiedAt on those links, and supersedes any
    /// prior current verification. Transactional.
    /// </summary>
    public async Task<MappingWriteResult> VerifyMapping(
        Guid clientId, string? evidenceNote, string verifiedBy, bool blastRadiusAcknowledged, CancellationToken ct = default)
    {
        if (!blastRadiusAcknowledged)
            return MappingWriteResult.Fail("Acknowledge the blast radius before verifying — that checkbox is the confirm step.");

        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = clock.UtcNow;

        var client = await db.Clients.FirstOrDefaultAsync(c => c.Id == clientId, ct);
        if (client is null)
        {
            await tx.RollbackAsync(ct);
            return MappingWriteResult.Fail("Client not found.");
        }

        var active = await db.IdentityLinks
            .Where(l => l.ClientId == clientId && l.InvalidatedAt == null)
            .ToListAsync(ct);

        var missing = RequiredLinks.All
            .Where(spec => !active.Any(l => l.System == spec.System && l.Kind == spec.Kind))
            .Select(spec => spec.Label)
            .ToList();
        if (missing.Count > 0)
        {
            await tx.RollbackAsync(ct);
            return MappingWriteResult.Fail($"Still missing: {string.Join(", ", missing)}. Link all four required identities before verifying.");
        }

        var requiredLinks = RequiredLinks.All
            .Select(spec => active.First(l => l.System == spec.System && l.Kind == spec.Kind))
            .ToList();

        // Supersede any prior current verification (one current verification per client).
        var priors = await db.MappingVerifications
            .Where(v => v.ClientId == clientId && v.InvalidatedAt == null)
            .ToListAsync(ct);
        foreach (var v in priors) v.InvalidatedAt = now;

        foreach (var l in requiredLinks) l.VerifiedAt = now;

        var json = JsonSerializer.Serialize(
            requiredLinks.Select(l => new { linkId = l.Id, linkVersion = l.LinkVersion }));

        db.MappingVerifications.Add(new MappingVerification
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            VerifiedLinksJson = json,
            EvidenceNote = string.IsNullOrWhiteSpace(evidenceNote) ? null : evidenceNote.Trim(),
            VerifiedBy = string.IsNullOrWhiteSpace(verifiedBy) ? DefaultActor : verifiedBy,
            BlastRadiusAcknowledged = true,
            VerifiedAt = now,
            InvalidatedAt = null,
        });

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return MappingWriteResult.Success("Mapping verified. This client is ready to promote to Assist.");
    }

    /// <summary>
    /// Promote to Assist — allowed ONLY with a current verification AND all
    /// required links present/active. Refuses otherwise with a clear reason.
    /// Never promotes past Assist (Auto is earned by clean days).
    /// </summary>
    public async Task<MappingWriteResult> PromoteToAssist(Guid clientId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var client = await db.Clients.FirstOrDefaultAsync(c => c.Id == clientId, ct);
        if (client is null)
            return MappingWriteResult.Fail("Client not found.");

        var hasVerification = await db.MappingVerifications
            .AnyAsync(v => v.ClientId == clientId && v.InvalidatedAt == null, ct);

        var active = await db.IdentityLinks
            .Where(l => l.ClientId == clientId && l.InvalidatedAt == null)
            .ToListAsync(ct);
        var requiredPresent = RequiredLinks.All
            .All(spec => active.Any(l => l.System == spec.System && l.Kind == spec.Kind));

        var (canPromote, reason) = PromotionGuard.CanPromote(hasVerification, requiredPresent);
        if (!canPromote)
            return MappingWriteResult.Fail(reason);

        if (client.EnforcementMode == EnforcementMode.Auto)
            return MappingWriteResult.Success("Already in Auto mode — the wizard never touches Auto.");
        if (client.EnforcementMode == EnforcementMode.Assist)
            return MappingWriteResult.Success("Already in Assist mode.");

        client.EnforcementMode = EnforcementMode.Assist;
        await db.SaveChangesAsync(ct);

        return MappingWriteResult.Success(
            "Promoted to Assist. The engine now proposes actions for a human to approve — still no autonomous writes.");
    }

    /// <summary>
    /// Resolve a duplicate-Stripe-customer investigation by keeping one customer:
    /// invalidate any other active Stripe Customer link for the client, and mark
    /// the investigation resolved with a note. Transactional.
    /// </summary>
    public async Task<MappingWriteResult> ResolveDuplicateStripe(Guid investigationId, string keepExternalId, CancellationToken ct = default)
    {
        keepExternalId = keepExternalId?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(keepExternalId))
            return MappingWriteResult.Fail("Choose which Stripe customer to keep.");

        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = clock.UtcNow;

        var item = await db.InvestigationItems.FirstOrDefaultAsync(i => i.Id == investigationId, ct);
        if (item is null || item.Status != InvestigationStatus.Open)
        {
            await tx.RollbackAsync(ct);
            return MappingWriteResult.Fail("That investigation was already handled (maybe in another tab).");
        }
        if (item.Kind != InvestigationKind.DuplicateStripeCustomer)
        {
            await tx.RollbackAsync(ct);
            return MappingWriteResult.Fail("This action only resolves duplicate-Stripe-customer items.");
        }

        if (item.ClientId is Guid cid)
        {
            var others = await db.IdentityLinks
                .Where(l => l.ClientId == cid && l.System == ExternalSystem.Stripe && l.Kind == LinkKind.Customer
                            && l.InvalidatedAt == null && l.ExternalId != keepExternalId)
                .ToListAsync(ct);
            foreach (var l in others) l.InvalidatedAt = now;
        }

        item.Status = InvestigationStatus.Resolved;
        item.ResolvedAt = now;
        item.ResolvedBy = DefaultActor;
        item.ResolutionNote = $"Kept Stripe customer {keepExternalId}; duplicate customer link(s) invalidated.";

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return MappingWriteResult.Success($"Resolved — kept Stripe customer {keepExternalId}.");
    }

    // ---------------- Helpers ----------------

    /// <summary>
    /// "Any required-link change invalidates and demotes atomically." Invalidates
    /// the client's current verification(s) and, if one existed, drops enforcement
    /// back to Shadow. Called inside the caller's transaction — no SaveChanges here.
    /// </summary>
    private static async Task InvalidateVerificationAndDemote(RdDbContext db, Client client, DateTimeOffset now, CancellationToken ct)
    {
        var currents = await db.MappingVerifications
            .Where(v => v.ClientId == client.Id && v.InvalidatedAt == null)
            .ToListAsync(ct);
        if (currents.Count == 0) return;

        foreach (var v in currents) v.InvalidatedAt = now;
        if (client.EnforcementMode != EnforcementMode.Shadow)
            client.EnforcementMode = EnforcementMode.Shadow;
    }
}
