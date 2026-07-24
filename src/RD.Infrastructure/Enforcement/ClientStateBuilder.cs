using Microsoft.EntityFrameworkCore;
using RD.Domain;
using RD.Domain.Entities;
using RD.Domain.Policy;

namespace RD.Infrastructure.Enforcement;

/// <summary>
/// Builds the pure-policy input (<see cref="ClientState"/>) from the SQL
/// projections. Shared by the policy heartbeat (evaluate-many) and the outbox
/// dispatcher (revalidate-one-immediately-before-write) so the verdict a human
/// approved is recomputed with the EXACT same logic at execution time — the
/// F4 guard against a world that changed between approval and write.
/// </summary>
public sealed class ClientStateBuilder(IClock clock, Microsoft.Extensions.Options.IOptions<EnforcementOptions> enforcement)
{
    private readonly EnforcementOptions _enf = enforcement.Value;

    /// <summary>Ambient facts shared across a batch — loaded once by the caller, not per client.</summary>
    public sealed record Context(DateTimeOffset? StripeSyncedAt, DateTimeOffset? MetaSyncedAt);

    public async Task<Context> LoadContextAsync(RdDbContext db, CancellationToken ct)
    {
        var stripe = await LatestCompleted(db, ExternalSystem.Stripe, ct);
        var meta = await LatestCompleted(db, ExternalSystem.Meta, ct);
        return new Context(stripe, meta);
    }

    /// <summary>Build one client's state. Loads only that client's rows — safe to call for a single revalidation.</summary>
    public async Task<ClientState> BuildAsync(RdDbContext db, Client client, Context ctx, CancellationToken ct)
    {
        var links = await db.IdentityLinks.AsNoTracking()
            .Where(l => l.ClientId == client.Id && l.InvalidatedAt == null)
            .ToListAsync(ct);

        var subLink = links.FirstOrDefault(l => l is { System: ExternalSystem.Stripe, Kind: LinkKind.Subscription });
        StripeSubscriptionProj? sub = null;
        if (subLink is not null)
            sub = await db.StripeSubscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.SubscriptionId == subLink.ExternalId, ct);

        var campaignIds = links.Where(l => l is { System: ExternalSystem.Meta, Kind: LinkKind.Campaign })
            .Select(l => l.ExternalId).ToList();
        var campaignProjs = await db.MetaCampaigns.AsNoTracking()
            .Where(c => campaignIds.Contains(c.CampaignId)).ToListAsync(ct);
        var appPaused = await db.PauseOperations.AsNoTracking()
            .Where(p => p.ClientId == client.Id && p.State == PauseOperationState.Paused)
            .Select(p => p.ExternalId).ToListAsync(ct);
        var appPausedSet = appPaused.ToHashSet();
        var campaigns = campaignProjs
            .Select(c => new CampaignState(c.CampaignId, c.EffectiveStatus, appPausedSet.Contains(c.CampaignId)))
            .ToList();

        var openInvoices = await db.StripeInvoices.AsNoTracking()
            .Where(i => i.ClientId == client.Id && (i.Status == "open" || i.Status == "uncollectible"))
            .ToListAsync(ct);

        var dunningCase = await db.DunningCases.AsNoTracking()
            .Where(c => c.ClientId == client.Id && c.Status == DunningCaseStatus.Open)
            .Include(c => c.Attempts)
            .OrderByDescending(c => c.OpenedAt)
            .FirstOrDefaultAsync(ct);

        var trial = await db.TrialPeriods.AsNoTracking()
            .Where(t => t.ClientId == client.Id && t.Outcome == TrialOutcome.Active)
            .OrderByDescending(t => t.StartsAt)
            .FirstOrDefaultAsync(ct);

        var ledger = await db.LedgerEntries.AsNoTracking()
            .Where(l => l.ClientId == client.Id)
            .GroupBy(l => l.Type)
            .Select(g => new { Type = g.Key, Sum = g.Sum(x => x.SignedAmount) })
            .ToListAsync(ct);
        var ledgerByType = ledger.ToDictionary(x => x.Type, x => x.Sum);
        var adSpend = -ledgerByType.GetValueOrDefault(LedgerEntryType.AdSpend);
        var paid = ledgerByType.GetValueOrDefault(LedgerEntryType.ChargePaid);

        var now = clock.UtcNow;
        var mappingComplete =
            links.Any(l => l is { System: ExternalSystem.Stripe, Kind: LinkKind.Customer }) &&
            subLink is not null &&
            campaignIds.Count > 0 &&
            links.Any(l => l is { System: ExternalSystem.Ghl, Kind: LinkKind.Contact });

        // Structural completeness feeds shadow evaluation. Human sign-off
        // (MappingVerification) gates ladder PROMOTION and is checked by the
        // wizard; here we require a current, non-invalidated verification so a
        // client whose links drifted (verification invalidated) falls back to
        // "unmapped" and the policy demotes.
        var hasCurrentVerification = await db.MappingVerifications.AsNoTracking()
            .AnyAsync(v => v.ClientId == client.Id && v.InvalidatedAt == null, ct);

        return new ClientState
        {
            ClientId = client.Id,
            Mode = client.EnforcementMode,
            Contract = client.ContractType,
            Account = client.AccountType,
            CurrencyCode = client.CurrencyCode,
            MappingVerified = mappingComplete && (client.EnforcementMode == EnforcementMode.Shadow || hasCurrentVerification),
            HasActiveTrial = trial is not null,
            TrialExpiresAt = trial?.ExpiresAt,
            TrialSpend = trial is null ? 0m : adSpend,
            TrialSpendCap = trial?.SpendCapSnapshot,
            SubscriptionStatus = sub?.Status,
            OpenUnpaidInvoices = openInvoices.Count(i => i.DueDate < now || i.Status == "uncollectible"),
            PaymentReceivedForCanceledSub = false, // webhook-era signal, populated by the Stripe receiver
            Dunning = dunningCase is null ? null : ToDunningState(dunningCase, _enf.DunningCadence),
            HasNewFailedCharge = dunningCase is null && openInvoices.Any(i => i.Status == "uncollectible"),
            Campaigns = campaigns,
            Exposure = Math.Max(0m, adSpend - paid),
            StripeSyncedAt = ctx.StripeSyncedAt,
            MetaSyncedAt = ctx.MetaSyncedAt,
            EvaluatedAt = now,
        };
    }

    /// <summary>
    /// Next-step timing is derived from a cadence, not from pre-created attempt
    /// rows: step 1 is due at OpenedAt, step k+1 at OpenedAt + k·cadence, and
    /// there is no next step once that time reaches the window. This keeps the
    /// schedule out of the database — attempts are created only when a step is
    /// actually staged.
    /// </summary>
    public static DunningState ToDunningState(DunningCase c, TimeSpan cadence)
    {
        var triggered = c.Attempts.Where(a => a.TriggeredAt != null).ToList();
        var lastStep = triggered.Count == 0 ? 0 : triggered.Max(a => a.Step);
        var nextDue = c.OpenedAt + cadence * lastStep;
        return new DunningState(
            LastStep: lastStep,
            WindowExpiresAt: c.WindowExpiresAt,
            NextStepDueAt: nextDue < c.WindowExpiresAt ? nextDue : null,
            AllSendsVerified: triggered.All(a => a.VerifiedAt != null),
            OldestUnverifiedSince: triggered.Where(a => a.VerifiedAt == null).Min(a => (DateTimeOffset?)a.TriggeredAt));
    }

    private static async Task<DateTimeOffset?> LatestCompleted(RdDbContext db, ExternalSystem system, CancellationToken ct) =>
        await db.SyncRuns.AsNoTracking()
            .Where(r => r.System == system && r.Status == SyncRunStatus.Completed)
            .MaxAsync(r => (DateTimeOffset?)r.CompletedAt, ct);
}
