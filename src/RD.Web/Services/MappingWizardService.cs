using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RD.Domain;
using RD.Domain.Entities;
using RD.Infrastructure;
using RD.Infrastructure.Enforcement;
using RD.Infrastructure.Gateways;

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
public class MappingWizardService(
    IDbContextFactory<RdDbContext> factory,
    IClock clock,
    VendorLinks vendorLinks,
    IOptions<StripeOptions> stripeOptions)
{
    /// <summary>Verifier stamp until Identity (real user names) lands — matches ReconciliationService.</summary>
    private const string DefaultActor = "operator";
    private const string RetiredClientMessage =
        "This client was merged into another account — update the surviving client instead.";

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
            .Where(c => c.AccountType == AccountType.Master && c.MergedIntoClientId == null)
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

        var masterClientIds = clients.Select(c => c.Id).ToList();
        var activeRequiredLinks = await db.IdentityLinks.AsNoTracking()
            .Where(l => masterClientIds.Contains(l.ClientId)
                        && l.InvalidatedAt == null
                        && ((l.System == ExternalSystem.Stripe
                             && (l.Kind == LinkKind.Customer || l.Kind == LinkKind.Subscription))
                            || (l.System == ExternalSystem.Meta && l.Kind == LinkKind.Campaign)
                            || (l.System == ExternalSystem.Ghl && l.Kind == LinkKind.Contact)))
            .ToListAsync(ct);
        var currentVerifications = await db.MappingVerifications.AsNoTracking()
            .Where(v => masterClientIds.Contains(v.ClientId) && v.InvalidatedAt == null)
            .OrderByDescending(v => v.VerifiedAt)
            .ToListAsync(ct);
        var verifiedClientIds = currentVerifications
            .GroupBy(v => v.ClientId)
            .Where(g => VerificationPinsAll(
                g.First().VerifiedLinksJson,
                activeRequiredLinks.Where(l => l.ClientId == g.Key)))
            .Select(g => g.Key)
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

        var ghlRows = await db.GhlMessages.AsNoTracking()
            .Where(m => contactIds.Contains(m.ContactId))
            .Select(m => new { m.ContactId, m.LocationId, m.SentAt })
            .ToListAsync(ct);
        var lastMessageByContact = ghlRows
            .GroupBy(m => m.ContactId)
            .ToDictionary(g => g.Key, g => (LastSentAt: g.Max(m => m.SentAt), Count: g.Count()));
        // GHL contact deep-links are location-scoped — resolve each contact's location from its observed messages.
        var ghlLocationByContact = ghlRows
            .GroupBy(m => m.ContactId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.SentAt).First().LocationId);

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
            Evidence: ResolveEvidence(l, subs, campaignFacts, lastMessageByContact, now, client.CurrencyCode),
            ExternalUrl: vendorLinks.For(
                l.System, l.Kind, l.ExternalId,
                l.System == ExternalSystem.Ghl && l.Kind == LinkKind.Contact
                    ? ghlLocationByContact.GetValueOrDefault(l.ExternalId)
                    : null)))
            .ToList();

        var requiredSlots = RequiredLinks.All.Select(spec =>
        {
            var slotLinks = linkViews
                .Where(v => v.System == spec.System && v.Kind == spec.Kind && !v.Invalidated)
                .OrderBy(v => v.ExternalId)
                .ToList();
            return new RequiredSlot(
                spec.System, spec.Kind, spec.Label, spec.HelpText,
                slotLinks.FirstOrDefault(), slotLinks.Skip(1).ToList());
        }).ToList();

        var blastRadius = BlastRadius.Compute(campaignFacts, client.CurrencyCode);

        var currentVerification = await db.MappingVerifications.AsNoTracking()
            .Where(v => v.ClientId == clientId && v.InvalidatedAt == null)
            .OrderByDescending(v => v.VerifiedAt)
            .FirstOrDefaultAsync(ct);
        if (currentVerification is not null
            && !VerificationPinsAll(currentVerification.VerifiedLinksJson, active))
            currentVerification = null;

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

    /// <summary>
    /// The client's active Stripe customers plus a conservative recommendation
    /// for the account to prefer for new subscriptions. Conflicting or stale evidence
    /// deliberately produces no recommendation.
    /// </summary>
    public async Task<StripeCustomerChoice> GetStripeCustomerChoice(Guid clientId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var client = await db.Clients.AsNoTracking()
            .Where(c => c.Id == clientId)
            .Select(c => new { c.PreferredStripeCustomerId })
            .FirstOrDefaultAsync(ct);

        var customerLinks = await db.IdentityLinks.AsNoTracking()
            .Where(l => l.ClientId == clientId && l.System == ExternalSystem.Stripe && l.Kind == LinkKind.Customer && l.InvalidatedAt == null)
            .OrderBy(l => l.ExternalId)
            .ToListAsync(ct);
        if (customerLinks.Count == 0)
            return new StripeCustomerChoice([], null, "No active Stripe customers are linked.", client?.PreferredStripeCustomerId);

        var now = clock.UtcNow;
        var completedStripeRun = await db.SyncRuns.AsNoTracking()
            .Where(r => r.System == ExternalSystem.Stripe && r.Status == SyncRunStatus.Completed && r.CompletedAt != null)
            .OrderByDescending(r => r.CompletedAt)
            .FirstOrDefaultAsync(ct);

        var customerIds = customerLinks.Select(l => l.ExternalId).ToList();
        var subscriptions = await db.StripeSubscriptions.AsNoTracking()
            .Where(s => customerIds.Contains(s.CustomerId))
            .ToListAsync(ct);
        var freshSubscriptions = completedStripeRun is null
            ? []
            : subscriptions.Where(s => s.SourceSyncedAt >= completedStripeRun.StartedAt && s.SourceSyncedAt <= now).ToList();

        var paidLookback = TimeSpan.FromDays(Math.Max(1, stripeOptions.Value.LedgerLookbackDays));
        var paidSince = now - paidLookback;
        var paidInvoices = completedStripeRun is null
            ? []
            : await db.StripeInvoices.AsNoTracking()
                .Where(i => customerIds.Contains(i.CustomerId)
                            && i.Status == "paid" && i.AmountPaid > 0m
                            && i.SubscriptionId != null && i.PaidAt != null
                            && i.PaidAt >= paidSince && i.PaidAt <= now)
                .ToListAsync(ct);

        var choice = BuildStripeCustomerChoice(
            customerLinks.Select(l => l.ExternalId), freshSubscriptions, paidInvoices,
            completedStripeRun?.CompletedAt, client?.PreferredStripeCustomerId, now, paidLookback);
        return choice with
        {
            CustomerLinks = customerLinks
                .Select(l => new StripeCustomerLinkStamp(l.Id, l.ExternalId, l.LinkVersion))
                .OrderBy(l => l.ExternalId, StringComparer.Ordinal)
                .ToList(),
        };
    }

    // ---------------- Writes ----------------

    /// <summary>
    /// Create or replace the client's link of a given system+kind. GHL Contact is
    /// the only single-cardinality kind; Stripe Customer/Subscription and Meta
    /// Campaign deliberately allow multiple active links so every account remains
    /// monitored. LinkVersion is bumped past the whole history.
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
        await using var mutationFence = await ClientMutationFence.AcquireAsync(db, clientId, ct);
        await using var ownershipFence = await ClientMutationFence.AcquireMappingOwnershipAsync(db, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = clock.UtcNow;

        var client = await db.Clients.FirstOrDefaultAsync(c => c.Id == clientId, ct);
        if (client is null)
        {
            await tx.RollbackAsync(ct);
            return MappingWriteResult.Fail("Client not found.");
        }
        if (client.MergedIntoClientId is not null)
        {
            await tx.RollbackAsync(ct);
            return MappingWriteResult.Fail(RetiredClientMessage);
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

        var singleCardinality = kind is LinkKind.Contact;
        var replaced = singleCardinality && existingActive.Count > 0;

        if (singleCardinality && existingActive.Count > 0)
        {
            // Invalidate the prior active link FIRST (and save) so the filtered
            // unique index (one active Contact per client)
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

        if (system == ExternalSystem.Stripe
            && kind == LinkKind.Customer
            && existingActive.Count > 0
            && !await db.InvestigationItems.AnyAsync(i =>
                i.ClientId == clientId
                && i.Kind == InvestigationKind.DuplicateStripeCustomer
                && i.Status == InvestigationStatus.Open, ct))
        {
            db.InvestigationItems.Add(new InvestigationItem
            {
                Id = Guid.NewGuid(),
                ClientId = clientId,
                Kind = InvestigationKind.DuplicateStripeCustomer,
                Detail = $"Stripe customer {externalId} was added after the prior ownership review. Confirm the complete customer set before new billing.",
                System = ExternalSystem.Stripe,
                ExternalId = externalId,
                CreatedAt = now,
            });
        }

        // Any required-link change demotes and invalidates the current verification.
        if (RequiredLinks.IsRequired(system, kind))
            await InvalidateVerificationAndDemote(db, client, now, ct);

        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(ct);
            return MappingWriteResult.Fail(
                "This client's mapping changed in another action. Refresh before adding the link again.");
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
        await using var mutationFence = await ClientMutationFence.AcquireAsync(db, clientId, ct);
        await using var ownershipFence = await ClientMutationFence.AcquireMappingOwnershipAsync(db, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = clock.UtcNow;

        var client = await db.Clients.FirstOrDefaultAsync(c => c.Id == clientId, ct);
        if (client is null)
        {
            await tx.RollbackAsync(ct);
            return MappingWriteResult.Fail("Client not found.");
        }
        if (client.MergedIntoClientId is not null)
        {
            await tx.RollbackAsync(ct);
            return MappingWriteResult.Fail(RetiredClientMessage);
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

        // Every active enforcement-relevant identity is part of the verification
        // batch. Pinning an arbitrary First() would leave additional Stripe
        // customers/subscriptions or Meta campaigns unverified even though they
        // affect ledger and enforcement behavior.
        var requiredLinks = active
            .Where(l => RequiredLinks.IsRequired(l.System, l.Kind))
            .OrderBy(l => l.System).ThenBy(l => l.Kind).ThenBy(l => l.ExternalId)
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
        await using var mutationFence = await ClientMutationFence.AcquireAsync(db, clientId, ct);
        await using var ownershipFence = await ClientMutationFence.AcquireMappingOwnershipAsync(db, ct);

        var client = await db.Clients.FirstOrDefaultAsync(c => c.Id == clientId, ct);
        if (client is null)
            return MappingWriteResult.Fail("Client not found.");
        if (client.MergedIntoClientId is not null)
            return MappingWriteResult.Fail(RetiredClientMessage);

        var active = await db.IdentityLinks
            .Where(l => l.ClientId == clientId && l.InvalidatedAt == null)
            .ToListAsync(ct);
        var currentVerification = await db.MappingVerifications.AsNoTracking()
            .Where(v => v.ClientId == clientId && v.InvalidatedAt == null)
            .OrderByDescending(v => v.VerifiedAt)
            .FirstOrDefaultAsync(ct);
        var hasVerification = currentVerification is not null
                              && VerificationPinsAll(currentVerification.VerifiedLinksJson, active);
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
    /// Resolve a multiple-Stripe-customer investigation by choosing the customer
    /// preferred for new subscriptions. Every customer/subscription link remains
    /// active so sync, ledger, and enforcement retain complete coverage.
    /// </summary>
    public async Task<MappingWriteResult> ResolveDuplicateStripe(
        Guid investigationId,
        string preferredExternalId,
        IReadOnlyCollection<StripeCustomerLinkStamp> expectedCustomerLinks,
        string actor = DefaultActor,
        CancellationToken ct = default)
    {
        preferredExternalId = preferredExternalId?.Trim() ?? "";
        actor = NormalizeActor(actor);
        if (string.IsNullOrWhiteSpace(preferredExternalId))
            return MappingWriteResult.Fail("Choose the Stripe customer to prefer for new subscriptions.");
        if (expectedCustomerLinks is null || expectedCustomerLinks.Count < 2)
            return MappingWriteResult.Fail(
                "The confirmed Stripe customer set is incomplete. Refresh the investigation and review every linked account.");

        var expectedLinkSet = expectedCustomerLinks
            .Select(l => (l.LinkId, l.ExternalId, l.LinkVersion))
            .ToHashSet();
        if (expectedLinkSet.Count != expectedCustomerLinks.Count
            || !expectedCustomerLinks.Any(l =>
                string.Equals(l.ExternalId, preferredExternalId, StringComparison.Ordinal)))
            return MappingWriteResult.Fail(
                "The confirmed Stripe customer set is invalid. Refresh the investigation and try again.");

        await using var db = await factory.CreateDbContextAsync(ct);
        var initialItem = await db.InvestigationItems.AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == investigationId, ct);
        if (initialItem is null || initialItem.Status != InvestigationStatus.Open)
            return MappingWriteResult.Fail("That investigation was already handled (maybe in another tab).");
        if (initialItem.Kind != InvestigationKind.DuplicateStripeCustomer)
            return MappingWriteResult.Fail("This action only resolves duplicate-Stripe-customer items.");
        if (initialItem.ClientId is not Guid clientId)
            return MappingWriteResult.Fail("This investigation is not attached to a client.");

        await using var mutationFence = await ClientMutationFence.AcquireAsync(db, clientId, ct);
        await using var ownershipFence = await ClientMutationFence.AcquireMappingOwnershipAsync(db, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = clock.UtcNow;

        var item = await db.InvestigationItems.FirstOrDefaultAsync(i => i.Id == investigationId, ct);
        if (item is null || item.Status != InvestigationStatus.Open)
        {
            await tx.RollbackAsync(ct);
            return MappingWriteResult.Fail("That investigation was already handled (maybe in another tab).");
        }
        if (item.Kind != InvestigationKind.DuplicateStripeCustomer || item.ClientId != clientId)
        {
            await tx.RollbackAsync(ct);
            return MappingWriteResult.Fail("The ownership investigation changed. Refresh and try again.");
        }

        var client = await db.Clients.FirstOrDefaultAsync(c => c.Id == clientId, ct);
        if (client is null)
        {
            await tx.RollbackAsync(ct);
            return MappingWriteResult.Fail("Client not found.");
        }
        if (client.MergedIntoClientId is not null)
        {
            await tx.RollbackAsync(ct);
            return MappingWriteResult.Fail(RetiredClientMessage);
        }

        var activeCustomerLinks = await db.IdentityLinks
            .Where(l => l.ClientId == clientId && l.System == ExternalSystem.Stripe
                        && l.Kind == LinkKind.Customer && l.InvalidatedAt == null)
            .ToListAsync(ct);
        var currentLinkSet = activeCustomerLinks
            .Select(l => (l.Id, l.ExternalId, l.LinkVersion))
            .ToHashSet();
        if (!currentLinkSet.SetEquals(expectedLinkSet))
        {
            await tx.RollbackAsync(ct);
            return MappingWriteResult.Fail(
                "Stripe customer links changed after you opened the review. Nothing was resolved; refresh and confirm the complete current set.");
        }

        var activeCustomerIds = activeCustomerLinks.Select(l => l.ExternalId).ToList();
        if (!activeCustomerIds.Contains(preferredExternalId, StringComparer.Ordinal))
        {
            await tx.RollbackAsync(ct);
            return MappingWriteResult.Fail("That Stripe customer is not an active link for this client. Refresh and choose again.");
        }

        RecordStripeCustomerPreference(
            db, client, preferredExternalId, actor, now,
            "Selected while resolving the multiple-Stripe-customer investigation.", item.Id);

        // Older verification batches pinned only one arbitrary link per kind.
        // If this confirmed cluster contains any required link the batch did not
        // show and pin, force an explicit re-verification before enforcement.
        var activeRequired = await db.IdentityLinks
            .Where(l => l.ClientId == clientId && l.InvalidatedAt == null)
            .ToListAsync(ct);
        var currentMappings = await db.MappingVerifications
            .Where(v => v.ClientId == clientId && v.InvalidatedAt == null)
            .ToListAsync(ct);
        if (currentMappings.Any(v => !VerificationPinsAll(v.VerifiedLinksJson, activeRequired)))
            await InvalidateVerificationAndDemote(db, client, now, ct);

        // The investigation item has no row-version column. Always touch the
        // owning client's row-version, even when this preference was already set,
        // so two tabs cannot both resolve the same open item successfully.
        db.Entry(client).Property(c => c.PreferredStripeCustomerId).IsModified = true;

        item.Status = InvestigationStatus.Resolved;
        item.ResolvedAt = now;
        item.ResolvedBy = actor;
        item.ResolutionNote =
            $"Confirmed as one business; preferred Stripe customer {preferredExternalId} for new subscriptions. " +
            $"All {activeCustomerIds.Count} linked Stripe customer account(s) remain active and monitored.";

        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(ct);
            return MappingWriteResult.Fail(
                "This client changed while you were resolving the Stripe-customer investigation. Refresh and try again.");
        }

        return MappingWriteResult.Success(
            $"Resolved — {preferredExternalId} is preferred for new subscriptions; all linked Stripe accounts remain monitored.");
    }

    /// <summary>
    /// Change the preferred Stripe customer after an investigation has been
    /// resolved. The choice must still be an active customer link and is written
    /// to append-only history; mappings and enforcement mode are untouched.
    /// </summary>
    public async Task<MappingWriteResult> ChangePreferredStripeCustomer(
        Guid clientId,
        string preferredExternalId,
        string actor = DefaultActor,
        CancellationToken ct = default)
    {
        preferredExternalId = preferredExternalId?.Trim() ?? "";
        actor = NormalizeActor(actor);
        if (string.IsNullOrWhiteSpace(preferredExternalId))
            return MappingWriteResult.Fail("Choose the Stripe customer to prefer for new subscriptions.");

        await using var db = await factory.CreateDbContextAsync(ct);
        await using var mutationFence = await ClientMutationFence.AcquireAsync(db, clientId, ct);
        await using var ownershipFence = await ClientMutationFence.AcquireMappingOwnershipAsync(db, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = clock.UtcNow;

        var client = await db.Clients.FirstOrDefaultAsync(c => c.Id == clientId, ct);
        if (client is null)
        {
            await tx.RollbackAsync(ct);
            return MappingWriteResult.Fail("Client not found.");
        }
        if (client.MergedIntoClientId is not null)
        {
            await tx.RollbackAsync(ct);
            return MappingWriteResult.Fail(RetiredClientMessage);
        }

        var activeCustomerIds = await db.IdentityLinks
            .Where(l => l.ClientId == clientId && l.System == ExternalSystem.Stripe
                        && l.Kind == LinkKind.Customer && l.InvalidatedAt == null)
            .Select(l => l.ExternalId)
            .ToListAsync(ct);
        if (!activeCustomerIds.Contains(preferredExternalId, StringComparer.Ordinal))
        {
            await tx.RollbackAsync(ct);
            return MappingWriteResult.Fail("That Stripe customer is not an active link for this client. Refresh and choose again.");
        }

        if (string.Equals(client.PreferredStripeCustomerId, preferredExternalId, StringComparison.Ordinal))
        {
            await tx.RollbackAsync(ct);
            return MappingWriteResult.Success($"{preferredExternalId} is already the preferred Stripe customer.");
        }

        RecordStripeCustomerPreference(
            db, client, preferredExternalId, actor, now,
            "Changed from the client details screen.", investigationItemId: null);

        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(ct);
            return MappingWriteResult.Fail(
                "This client changed while you were setting its preferred Stripe customer. Refresh and try again.");
        }

        return MappingWriteResult.Success(
            $"Preferred Stripe customer changed to {preferredExternalId}. All {activeCustomerIds.Count} linked accounts remain monitored.");
    }

    /// <summary>
    /// Apply only unambiguous, fresh-evidence preferences to clients in the open
    /// multiple-customer queue. This deliberately does NOT resolve the queue item:
    /// billing evidence can choose a customer for new subscriptions, but cannot
    /// prove that every linked account belongs to the same business. Existing
    /// valid operator preferences always win over a recommendation.
    /// </summary>
    public async Task<StripeCustomerBulkResult> ApplySafeStripeCustomerPreferences(
        string actor = DefaultActor,
        CancellationToken ct = default)
    {
        actor = NormalizeActor(actor);
        await using var db = await factory.CreateDbContextAsync(ct);
        var discoveredClientIds = await db.InvestigationItems.AsNoTracking()
            .Where(i => i.Kind == InvestigationKind.DuplicateStripeCustomer
                        && i.Status == InvestigationStatus.Open && i.ClientId != null)
            .Select(i => i.ClientId!.Value)
            .Distinct()
            .OrderBy(id => id)
            .ToListAsync(ct);
        if (discoveredClientIds.Count == 0)
            return new StripeCustomerBulkResult(true, 0, 0, 0, "There are no open multiple-Stripe-customer items.");

        // Lock the complete discovered set before reading any evidence used by
        // the write. A client added to the queue after this snapshot is safely
        // left for the next run; every client changed by this run is fenced.
        await using var mutationFences = await ClientMutationFence.AcquireManyAsync(db, discoveredClientIds, ct);
        await using var ownershipFence = await ClientMutationFence.AcquireMappingOwnershipAsync(db, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = clock.UtcNow;

        var items = await db.InvestigationItems
            .Where(i => discoveredClientIds.Contains(i.ClientId!.Value)
                        && i.Kind == InvestigationKind.DuplicateStripeCustomer
                        && i.Status == InvestigationStatus.Open && i.ClientId != null)
            .OrderBy(i => i.CreatedAt)
            .ToListAsync(ct);
        if (items.Count == 0)
        {
            await tx.RollbackAsync(ct);
            return new StripeCustomerBulkResult(true, 0, 0, 0, "There are no open multiple-Stripe-customer items.");
        }

        var clientIds = items.Select(i => i.ClientId!.Value).Distinct().ToList();
        var clients = await db.Clients
            .Where(c => clientIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, ct);
        var retiredClientCount = clients.Values.Count(client => client.MergedIntoClientId is not null);
        if (retiredClientCount > 0)
        {
            await tx.RollbackAsync(ct);
            return new StripeCustomerBulkResult(
                false,
                0,
                0,
                clientIds.Count,
                $"No preferences were changed because {retiredClientCount} queued client(s) were merged into another account. Review the surviving clients instead.");
        }

        var completedStripeRun = await db.SyncRuns.AsNoTracking()
            .Where(r => r.System == ExternalSystem.Stripe && r.Status == SyncRunStatus.Completed && r.CompletedAt != null)
            .OrderByDescending(r => r.CompletedAt)
            .FirstOrDefaultAsync(ct);
        if (completedStripeRun?.CompletedAt is not { } completed
            || completed > now
            || now - completed > StripeCustomerRecommendationRules.DefaultSyncFreshnessBound)
        {
            await tx.RollbackAsync(ct);
            return new StripeCustomerBulkResult(
                false, 0, 0, items.Select(i => i.ClientId).Distinct().Count(),
                "No records were changed because Stripe evidence is not fresh. Run a successful Stripe sync, then try again.");
        }

        var customerLinks = await db.IdentityLinks.AsNoTracking()
            .Where(l => clientIds.Contains(l.ClientId) && l.System == ExternalSystem.Stripe
                        && l.Kind == LinkKind.Customer && l.InvalidatedAt == null)
            .Select(l => new { l.ClientId, l.ExternalId })
            .ToListAsync(ct);
        var customerIds = customerLinks.Select(l => l.ExternalId).Distinct().ToList();
        var subscriptions = await db.StripeSubscriptions.AsNoTracking()
            .Where(s => customerIds.Contains(s.CustomerId))
            .ToListAsync(ct);
        var freshSubscriptions = subscriptions
            .Where(s => s.SourceSyncedAt >= completedStripeRun.StartedAt && s.SourceSyncedAt <= now)
            .ToList();

        var paidLookback = TimeSpan.FromDays(Math.Max(1, stripeOptions.Value.LedgerLookbackDays));
        var paidSince = now - paidLookback;
        var paidInvoices = await db.StripeInvoices.AsNoTracking()
            .Where(i => customerIds.Contains(i.CustomerId)
                        && i.Status == "paid" && i.AmountPaid > 0m
                        && i.SubscriptionId != null && i.PaidAt != null
                        && i.PaidAt >= paidSince && i.PaidAt <= now)
            .ToListAsync(ct);

        var linksByClient = customerLinks
            .GroupBy(l => l.ClientId)
            .ToDictionary(g => g.Key, g => g.Select(l => l.ExternalId).Distinct(StringComparer.Ordinal).ToList());
        var applied = 0;
        var alreadyPreferred = 0;
        var needsReview = 0;

        foreach (var itemGroup in items.GroupBy(i => i.ClientId!.Value))
        {
            var clientId = itemGroup.Key;
            var item = itemGroup.First();
            if (!clients.TryGetValue(clientId, out var client)
                || !linksByClient.TryGetValue(clientId, out var linkedIds)
                || linkedIds.Count < 2)
            {
                needsReview++;
                continue;
            }

            var choice = BuildStripeCustomerChoice(
                linkedIds,
                freshSubscriptions.Where(s => linkedIds.Contains(s.CustomerId, StringComparer.Ordinal)).ToList(),
                paidInvoices.Where(i => linkedIds.Contains(i.CustomerId, StringComparer.Ordinal)).ToList(),
                completedStripeRun.CompletedAt,
                client.PreferredStripeCustomerId,
                now,
                paidLookback);

            var validCurrentPreference = client.PreferredStripeCustomerId is { Length: > 0 } current
                                         && linkedIds.Contains(current, StringComparer.Ordinal)
                ? current
                : null;
            if (validCurrentPreference is not null)
            {
                alreadyPreferred++;
                continue;
            }

            // The subscription sweep is complete and fresh, so a sole active or
            // trialing owner is safe to apply automatically. Paid-invoice-only
            // suggestions remain manual because Stripe lists invoices by creation
            // time, not payment time, so absence is not complete evidence.
            if (!choice.CanAutoApply || choice.RecommendedExternalId is not { } selected)
            {
                needsReview++;
                continue;
            }

            RecordStripeCustomerPreference(
                db, client, selected, actor, now,
                $"Applied safe recommendation from open ownership investigation; investigation remains open: {choice.RecommendationReason}",
                item.Id);
            applied++;
        }

        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(ct);
            return new StripeCustomerBulkResult(
                false,
                0,
                0,
                clientIds.Count,
                "No preferences were changed because at least one client changed during this bulk action. Refresh and try again.");
        }

        return new StripeCustomerBulkResult(
            true,
            applied,
            alreadyPreferred,
            needsReview,
            $"Set {applied} safe preferred customer(s); {alreadyPreferred} already had a preference and {needsReview} need manual preference review. " +
            "Ownership investigations remain open until an operator confirms the same-business cases; separate-business cases require manual mapping correction. All linked accounts remain monitored.");
    }

    // ---------------- Helpers ----------------

    private static bool VerificationPinsAll(
        string verifiedLinksJson, IEnumerable<IdentityLink> activeLinks) =>
        MappingVerificationCoverage.PinsAll(
            verifiedLinksJson,
            activeLinks
                .Where(l => RequiredLinks.IsRequired(l.System, l.Kind))
                .Select(l => (l.Id, l.LinkVersion)));

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
        foreach (var v in currents) v.InvalidatedAt = now;
        if (client.EnforcementMode != EnforcementMode.Shadow)
            client.EnforcementMode = EnforcementMode.Shadow;

        // Force a row-version bump even when the client was already Shadow and
        // had no current verification. Confirmation workflows use that token as
        // a second line of defense against concurrent required-link changes.
        db.Entry(client).Property(c => c.EnforcementMode).IsModified = true;

        // Dispatcher policy revalidation intentionally lets Shadow clients be
        // evaluated structurally. Therefore demotion alone cannot retire work
        // approved against an earlier identity set: explicitly supersede every
        // nonterminal action before the mapping change commits.
        var queuedActions = await db.OutboxActions
            .Where(action => action.ClientId == client.Id
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
            action.LastError =
                "Superseded because an enforcement-relevant identity mapping changed; re-verify the complete mapping before staging a new action.";
        }
    }

    private static void RecordStripeCustomerPreference(
        RdDbContext db,
        Client client,
        string preferredExternalId,
        string actor,
        DateTimeOffset now,
        string reason,
        Guid? investigationItemId)
    {
        if (string.Equals(client.PreferredStripeCustomerId, preferredExternalId, StringComparison.Ordinal))
            return;

        db.StripeCustomerPreferenceChanges.Add(new StripeCustomerPreferenceChange
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            PreviousStripeCustomerId = client.PreferredStripeCustomerId,
            PreferredStripeCustomerId = preferredExternalId,
            ChangedBy = actor,
            ChangedAt = now,
            Reason = reason,
            InvestigationItemId = investigationItemId,
        });
        client.PreferredStripeCustomerId = preferredExternalId;
    }

    private static StripeCustomerChoice BuildStripeCustomerChoice(
        IEnumerable<string> customerIds,
        IReadOnlyCollection<StripeSubscriptionProj> subscriptions,
        IReadOnlyCollection<StripeInvoiceProj> paidInvoices,
        DateTimeOffset? completedStripeSyncAt,
        string? currentPreferredExternalId,
        DateTimeOffset now,
        TimeSpan paidLookback)
    {
        var ids = customerIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        var evidence = ids.Select(id => new StripeCustomerRecommendationEvidence(
            id,
            subscriptions.Where(s => string.Equals(s.CustomerId, id, StringComparison.Ordinal))
                .Select(s => s.Status).ToList(),
            paidInvoices.Where(i => string.Equals(i.CustomerId, id, StringComparison.Ordinal) && i.PaidAt != null)
                .Select(i => i.PaidAt!.Value).ToList())).ToList();

        var recommendation = StripeCustomerRecommendationRules.Recommend(
            new StripeCustomerRecommendationInput(completedStripeSyncAt, evidence),
            now,
            StripeCustomerRecommendationRules.DefaultSyncFreshnessBound,
            paidLookback);
        var recommendationReason = DescribeRecommendation(recommendation, evidence);

        var candidates = evidence.Select(e =>
        {
            var statuses = e.SubscriptionStatuses
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var latestPaid = e.PaidSubscriptionInvoiceAt.Count == 0
                ? (DateTimeOffset?)null
                : e.PaidSubscriptionInvoiceAt.Max();
            var subscriptionDetail = statuses.Count == 0
                ? "No subscriptions synced"
                : $"{e.SubscriptionStatuses.Count} subscription(s): {string.Join(", ", statuses)}";
            var invoiceDetail = latestPaid is null
                ? "no recent paid subscription invoice"
                : $"latest paid subscription invoice {latestPaid:yyyy-MM-dd}";
            return new StripeCustomerCandidate(e.ExternalId, $"{subscriptionDetail} · {invoiceDetail}", statuses, latestPaid);
        })
        .OrderByDescending(c => string.Equals(c.ExternalId, recommendation.RecommendedExternalId, StringComparison.Ordinal))
        .ThenBy(c => c.ExternalId, StringComparer.Ordinal)
        .ToList();

        return new StripeCustomerChoice(
            candidates,
            recommendation.RecommendedExternalId,
            recommendationReason,
            currentPreferredExternalId,
            CanAutoApply: recommendation.Reason == StripeCustomerRecommendationReason.ActiveOrTrialingSubscription);
    }

    private static string DescribeRecommendation(
        StripeCustomerRecommendation recommendation,
        IReadOnlyCollection<StripeCustomerRecommendationEvidence> evidence)
        => recommendation.Reason switch
        {
            StripeCustomerRecommendationReason.ActiveOrTrialingSubscription =>
                "Recommended because this is the only account with an active or trialing subscription.",
            StripeCustomerRecommendationReason.RecentPaidSubscriptionInvoice =>
                $"Suggested because it is the only account with a successful recent subscription payment currently synced" +
                (recommendation.RecommendedExternalId is { } id
                 && evidence.FirstOrDefault(e => e.ExternalId == id)?.PaidSubscriptionInvoiceAt.Max() is { } paidAt
                    ? $" ({paidAt:yyyy-MM-dd})."
                    : "."),
            StripeCustomerRecommendationReason.MultipleBillingOwners =>
                "No recommendation: more than one account has a current, non-terminal subscription.",
            StripeCustomerRecommendationReason.MultipleRecentPaidOwners =>
                "No recommendation: more than one account received a recent subscription payment.",
            StripeCustomerRecommendationReason.ConflictingSubscriptionAndPaidOwners =>
                "No recommendation: the current subscription and recent payment point to different accounts.",
            StripeCustomerRecommendationReason.StaleCompletedStripeSync =>
                "No recommendation: Stripe data is stale. Run a successful sync first.",
            StripeCustomerRecommendationReason.NoCompletedStripeSync =>
                "No recommendation: no completed Stripe sync is available.",
            StripeCustomerRecommendationReason.FutureCompletedStripeSync =>
                "No recommendation: the Stripe sync timestamp is invalid.",
            _ => "No recommendation: there is not enough current billing evidence to choose safely.",
        };

    private static string NormalizeActor(string? actor)
    {
        var normalized = string.IsNullOrWhiteSpace(actor) ? DefaultActor : actor.Trim();
        return normalized.Length <= 100 ? normalized : normalized[..100];
    }
}
