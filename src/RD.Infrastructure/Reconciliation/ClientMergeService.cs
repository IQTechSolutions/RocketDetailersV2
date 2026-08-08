using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using RD.Domain;
using RD.Domain.Entities;
using RD.Infrastructure.Enforcement;

namespace RD.Infrastructure.Reconciliation;

/// <summary>What a merge would move — shown to the operator before they confirm.</summary>
public sealed record MergePreview(
    Guid SurvivorId,
    Guid DuplicateId,
    string SurvivorName,
    string DuplicateName,
    int LinksToMove,
    int LedgerRowsToRollUp,
    int TrialsToMove,
    int OpenInvestigationsToClose,
    string? Blocked);

/// <summary>What an unmerge would restore — shown to the operator before they confirm.</summary>
public sealed record UnmergePreview(
    Guid DuplicateId,
    Guid? SurvivorId,
    string DuplicateName,
    string SurvivorName,
    int LinksToRestore,
    int TrialsToRestore,
    int InvestigationsToReopen,
    string? Blocked);

/// <summary>Outcome of a merge — success flag plus a human sentence and the survivor to navigate to.</summary>
public sealed record MergeResult(bool Ok, string Message, Guid? SurvivorId = null);

/// <summary>Outcome of an unmerge — success flag, a human sentence, and the restored client to navigate to.</summary>
public sealed record UnmergeResult(bool Ok, string Message, Guid? RestoredClientId = null);

/// <summary>
/// Everything a merge moved or changed, captured at merge time so the merge can be
/// reversed deterministically. Moved rows carry no origin marker once re-parented,
/// so an unmerge that doesn't have this would have to guess which of the survivor's
/// rows were the duplicate's. Persisted as JSON on <see cref="ClientMergeAudit"/>.
/// </summary>
public sealed record MergeSnapshot
{
    // Field-name tokens recorded in BackfilledFields when the merge filled a blank
    // survivor field from the duplicate — used to null them back out on unmerge.
    public const string FieldContactName = "ContactName";
    public const string FieldEmail = "Email";
    public const string FieldPhone = "Phone";
    public const string FieldCountry = "Country";
    public const string FieldPackage = "PackageId";
    public const string FieldArrangement = "Arrangement";

    public Guid[] LinkIds { get; init; } = [];
    public string[] SubscriptionIds { get; init; } = [];
    public string[] InvoiceIds { get; init; } = [];
    public Guid[] TrialIds { get; init; } = [];
    public Guid[] PauseIds { get; init; } = [];
    public Guid[] DunningIds { get; init; } = [];
    public Guid[] ClosedInvestigationIds { get; init; } = [];
    public Guid[] InvalidatedVerificationIds { get; init; } = [];
    public Guid[] KnownLedgerEntryIds { get; init; } = [];
    public Guid[] KnownIdentityLinkIds { get; init; } = [];
    public Guid[] KnownConvertIntentIds { get; init; } = [];
    public Guid[] KnownTrialIds { get; init; } = [];
    public Guid[] KnownPauseIds { get; init; } = [];
    public Guid[] KnownDunningIds { get; init; } = [];
    public Guid[] KnownInvestigationIds { get; init; } = [];
    public Guid[] KnownOutboxIds { get; init; } = [];
    public string[] KnownStripeSubscriptionIds { get; init; } = [];
    public string[] KnownStripeInvoiceIds { get; init; } = [];

    /// <summary>Blank survivor fields the merge filled from the duplicate (see the Field* tokens).</summary>
    public string[] BackfilledFields { get; init; } = [];

    /// <summary>The exact merge line appended to the survivor's notes, so unmerge can strip it back off.</summary>
    public string? NoteLineAppended { get; init; }

    /// <summary>Whether the survivor's notes were empty before the merge (so unmerge nulls them vs. trims the appended line).</summary>
    public bool NotesWasEmpty { get; init; }

    /// <summary>The duplicate's pre-merge mode retained for audit; unmerge stays Shadow until re-verification.</summary>
    public EnforcementMode DuplicatePriorMode { get; init; }
}

/// <summary>
/// Merges a duplicate client record into a surviving one, and reverses that merge.
/// The duplicate is a real business whose Stripe/Meta/GHL records are LIVE — it is
/// retired, never deleted, so its third-party movement keeps being monitored.
///
/// Because the sync resolves every external object to a client purely through
/// <see cref="IdentityLink"/>, re-parenting the duplicate's links onto the survivor
/// makes ALL future charges, campaigns and messages attribute to the survivor on
/// the next sweep — no per-sync special-casing. The mutable projections are
/// re-stamped here too so the survivor's balance and eligibility are correct
/// immediately rather than only after the next cycle.
///
/// The append-only ledger cannot be rewritten, so the duplicate's historical
/// entries keep their ClientId and are rolled up into the survivor at read time
/// (see <see cref="Enforcement.ClientStateBuilder"/>) via the MergedIntoClientId
/// pointer. That pointer also drops the duplicate out of every active surface.
///
/// Reversible by design: every merge writes a <see cref="ClientMergeAudit"/> snapshot
/// of exactly what it moved, and <see cref="UnmergeAsync"/> replays it in reverse.
/// </summary>
public sealed class ClientMergeService(IDbContextFactory<RdDbContext> factory, IClock clock)
{
    private const string BillingRelevantConversionBlock =
        "One of these accounts has a conversion that can still bill or receive a late payment. Resolve or reconcile it before changing client ownership.";
    private const string PostMergeActivityBlock =
        "This merge can only be reversed before either account records post-merge billing or operational activity. Reconcile this merge manually so immutable history is not assigned to the wrong client.";

    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<MergePreview> PreviewAsync(Guid survivorId, Guid duplicateId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var survivor = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == survivorId, ct);
        var duplicate = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == duplicateId, ct);

        var blocked = Validate(survivorId, duplicateId, survivor, duplicate,
            await AbsorbedOthers(db, duplicateId, ct));
        if (blocked is null
            && await HasBillingRelevantConversionAsync(db, survivorId, duplicateId, ct))
        {
            blocked = BillingRelevantConversionBlock;
        }

        var links = await db.IdentityLinks.CountAsync(l => l.ClientId == duplicateId && l.InvalidatedAt == null, ct);
        var ledger = await db.LedgerEntries.CountAsync(l => l.ClientId == duplicateId, ct);
        var trials = await db.TrialPeriods.CountAsync(t => t.ClientId == duplicateId, ct);
        var openItems = await db.InvestigationItems.CountAsync(i => i.ClientId == duplicateId && i.Status == InvestigationStatus.Open, ct);

        return new MergePreview(
            survivorId, duplicateId,
            survivor?.BusinessName ?? "(unknown)", duplicate?.BusinessName ?? "(unknown)",
            links, ledger, trials, openItems, blocked);
    }

    public async Task<MergeResult> MergeAsync(Guid survivorId, Guid duplicateId, string actor, CancellationToken ct = default)
    {
        if (survivorId == duplicateId)
            return new MergeResult(false, "A client can't be merged into itself.");

        await using var db = await factory.CreateDbContextAsync(ct);
        var (firstFenceId, secondFenceId) = OrderedClientIds(survivorId, duplicateId);
        await using var firstFence = await ClientMutationFence.AcquireAsync(db, firstFenceId, ct);
        await using var secondFence = await ClientMutationFence.AcquireAsync(db, secondFenceId, ct);
        await using var ownershipFence = await ClientMutationFence.AcquireMappingOwnershipAsync(db, ct);
        var now = clock.UtcNow;

        var survivor = await db.Clients.FirstOrDefaultAsync(c => c.Id == survivorId, ct);
        var duplicate = await db.Clients.FirstOrDefaultAsync(c => c.Id == duplicateId, ct);

        var blocked = Validate(survivorId, duplicateId, survivor, duplicate,
            await AbsorbedOthers(db, duplicateId, ct));
        if (blocked is not null) return new MergeResult(false, blocked);
        if (await HasBillingRelevantConversionAsync(db, survivorId, duplicateId, ct))
            return new MergeResult(false, BillingRelevantConversionBlock);

        // 1. Re-parent every identity link — the core move. Future sync sweeps
        //    resolve these vendor ids to the survivor from now on.
        var links = await db.IdentityLinks.Where(l => l.ClientId == duplicateId).ToListAsync(ct);
        foreach (var l in links) { l.ClientId = survivorId; l.LinkVersion++; }

        // 2. Re-stamp the mutable projections so the survivor is correct immediately
        //    (the sweep would re-stamp them within the cycle anyway, but a paused
        //    client shouldn't wait a cycle to reflect the truth).
        var subs = await db.StripeSubscriptions.Where(s => s.ClientId == duplicateId).ToListAsync(ct);
        foreach (var s in subs) s.ClientId = survivorId;
        var invoices = await db.StripeInvoices.Where(i => i.ClientId == duplicateId).ToListAsync(ct);
        foreach (var i in invoices) i.ClientId = survivorId;

        // 3. Move child + enforcement rows that key on ClientId.
        var trials = await db.TrialPeriods.Where(t => t.ClientId == duplicateId).ToListAsync(ct);
        foreach (var t in trials) t.ClientId = survivorId;
        var pauses = await db.PauseOperations.Where(p => p.ClientId == duplicateId).ToListAsync(ct);
        foreach (var p in pauses) p.ClientId = survivorId;
        var dunning = await db.DunningCases.Where(d => d.ClientId == duplicateId).ToListAsync(ct);
        foreach (var d in dunning) d.ClientId = survivorId;

        // 4. Close the duplicate's open investigation items — they were about the
        //    duplicate identity, which no longer stands alone.
        var closedItems = await db.InvestigationItems.Where(i => i.ClientId == duplicateId && i.Status == InvestigationStatus.Open).ToListAsync(ct);
        foreach (var item in closedItems)
        {
            item.Status = InvestigationStatus.Resolved;
            item.ResolvedAt = now;
            item.ResolvedBy = actor;
            item.ResolutionNote = $"Resolved by merge into \"{survivor!.BusinessName}\".";
        }

        // 5. Invalidate both accounts' mapping verifications because their
        //    required-link sets changed and prior sign-off no longer describes reality.
        var affectedClientIds = new[] { survivorId, duplicateId };
        var invalidatedVerifications = await db.MappingVerifications
            .Where(v => affectedClientIds.Contains(v.ClientId) && v.InvalidatedAt == null)
            .ToListAsync(ct);
        foreach (var v in invalidatedVerifications) v.InvalidatedAt = now;

        // 6. Survivor wins; fill only its blank profile fields from the duplicate.
        //    Record which blanks we filled so an unmerge can null them back out.
        var backfilled = new List<string>();
        if (string.IsNullOrWhiteSpace(survivor!.ContactName) && !string.IsNullOrWhiteSpace(duplicate!.ContactName))
        { survivor.ContactName = duplicate.ContactName; backfilled.Add(MergeSnapshot.FieldContactName); }
        if (string.IsNullOrWhiteSpace(survivor.Email) && !string.IsNullOrWhiteSpace(duplicate!.Email))
        { survivor.Email = duplicate.Email; backfilled.Add(MergeSnapshot.FieldEmail); }
        if (string.IsNullOrWhiteSpace(survivor.Phone) && !string.IsNullOrWhiteSpace(duplicate!.Phone))
        { survivor.Phone = duplicate.Phone; backfilled.Add(MergeSnapshot.FieldPhone); }
        if (string.IsNullOrWhiteSpace(survivor.Country) && !string.IsNullOrWhiteSpace(duplicate!.Country))
        { survivor.Country = duplicate.Country; backfilled.Add(MergeSnapshot.FieldCountry); }
        if (survivor.PackageId is null && duplicate!.PackageId is not null)
        { survivor.PackageId = duplicate.PackageId; backfilled.Add(MergeSnapshot.FieldPackage); }
        // Arrangement is a profile field too: adopt the duplicate's only if the
        // survivor has none inferred/confirmed yet.
        if (survivor.ExpectedAmount is null && survivor.ArrangementStatus == ArrangementStatus.Unknown && duplicate!.ExpectedAmount is not null)
        {
            survivor.ExpectedAmount = duplicate.ExpectedAmount;
            survivor.CadenceDays = duplicate.CadenceDays;
            survivor.ArrangementStatus = duplicate.ArrangementStatus;
            backfilled.Add(MergeSnapshot.FieldArrangement);
        }

        var noteLine = $"Merged in \"{duplicate!.BusinessName}\" ({duplicateId}) on {now:yyyy-MM-dd} by {actor}.";
        var notesWasEmpty = string.IsNullOrWhiteSpace(survivor.Notes);
        survivor.Notes = notesWasEmpty ? noteLine : survivor.Notes + "\n" + noteLine;

        // 7. Retire the duplicate: the pointer drops it from every active surface,
        //    and Shadow makes it inert even if something evaluates it directly.
        var priorMode = duplicate.EnforcementMode;
        duplicate.MergedIntoClientId = survivorId;
        duplicate.MergedAt = now;
        duplicate.EnforcementMode = EnforcementMode.Shadow;
        survivor.EnforcementMode = EnforcementMode.Shadow;

        await SupersedeNonterminalActionsAsync(
            db,
            affectedClientIds,
            "Superseded because a client merge changed the complete identity mapping; re-verify before staging new work.",
            ct);

        // 8. Record exactly what moved, so the merge is deterministically reversible.
        var snapshot = new MergeSnapshot
        {
            LinkIds = links.Select(l => l.Id).ToArray(),
            SubscriptionIds = subs.Select(s => s.SubscriptionId).ToArray(),
            InvoiceIds = invoices.Select(i => i.InvoiceId).ToArray(),
            TrialIds = trials.Select(t => t.Id).ToArray(),
            PauseIds = pauses.Select(p => p.Id).ToArray(),
            DunningIds = dunning.Select(d => d.Id).ToArray(),
            ClosedInvestigationIds = closedItems.Select(i => i.Id).ToArray(),
            InvalidatedVerificationIds = invalidatedVerifications.Select(v => v.Id).ToArray(),
            KnownLedgerEntryIds = await db.LedgerEntries
                .Where(row => affectedClientIds.Contains(row.ClientId))
                .Select(row => row.Id).ToArrayAsync(ct),
            KnownIdentityLinkIds = await db.IdentityLinks
                .Where(link => affectedClientIds.Contains(link.ClientId))
                .Select(link => link.Id).ToArrayAsync(ct),
            KnownConvertIntentIds = await db.ConvertIntents
                .Where(intent => affectedClientIds.Contains(intent.ClientId))
                .Select(intent => intent.Id).ToArrayAsync(ct),
            KnownTrialIds = await db.TrialPeriods
                .Where(trial => affectedClientIds.Contains(trial.ClientId))
                .Select(trial => trial.Id).ToArrayAsync(ct),
            KnownPauseIds = await db.PauseOperations
                .Where(pause => affectedClientIds.Contains(pause.ClientId))
                .Select(pause => pause.Id).ToArrayAsync(ct),
            KnownDunningIds = await db.DunningCases
                .Where(dunningCase => affectedClientIds.Contains(dunningCase.ClientId))
                .Select(dunningCase => dunningCase.Id).ToArrayAsync(ct),
            KnownInvestigationIds = await db.InvestigationItems
                .Where(item => item.ClientId != null
                               && affectedClientIds.Contains(item.ClientId.Value))
                .Select(item => item.Id).ToArrayAsync(ct),
            KnownOutboxIds = await db.OutboxActions
                .Where(action => affectedClientIds.Contains(action.ClientId))
                .Select(action => action.Id).ToArrayAsync(ct),
            KnownStripeSubscriptionIds = await db.StripeSubscriptions
                .Where(subscription => subscription.ClientId != null
                                       && affectedClientIds.Contains(subscription.ClientId.Value))
                .Select(subscription => subscription.SubscriptionId).ToArrayAsync(ct),
            KnownStripeInvoiceIds = await db.StripeInvoices
                .Where(invoice => invoice.ClientId != null
                                  && affectedClientIds.Contains(invoice.ClientId.Value))
                .Select(invoice => invoice.InvoiceId).ToArrayAsync(ct),
            BackfilledFields = [.. backfilled],
            NoteLineAppended = noteLine,
            NotesWasEmpty = notesWasEmpty,
            DuplicatePriorMode = priorMode,
        };
        db.ClientMergeAudits.Add(new ClientMergeAudit
        {
            Id = Guid.NewGuid(),
            SurvivorId = survivorId,
            DuplicateId = duplicateId,
            MergedBy = actor,
            MergedAt = now,
            SnapshotJson = JsonSerializer.Serialize(snapshot, Json),
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new MergeResult(false, "One of these accounts changed while merging — reload and try again.");
        }

        return new MergeResult(true,
            $"Merged \"{duplicate.BusinessName}\" into \"{survivor.BusinessName}\". {links.Count} link(s) moved; the duplicate is retired but still monitored.",
            survivorId);
    }

    public async Task<UnmergePreview> UnmergePreviewAsync(Guid duplicateId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var duplicate = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == duplicateId, ct);
        var audit = await LiveAudit(db, duplicateId, ct);

        var (blocked, survivorName) = await ValidateUnmerge(db, duplicate, audit, ct);
        var snapshot = audit is not null ? Deserialize(audit) : null;
        if (blocked is null && audit is not null)
        {
            if (await HasBillingRelevantConversionAsync(db, duplicateId, audit.SurvivorId, ct))
                blocked = BillingRelevantConversionBlock;
            else if (snapshot is not null
                     && await HasPostMergeActivityAsync(
                         db, duplicateId, audit.SurvivorId, snapshot, ct))
                blocked = PostMergeActivityBlock;
        }

        return new UnmergePreview(
            duplicateId, audit?.SurvivorId,
            duplicate?.BusinessName ?? "(unknown)", survivorName,
            snapshot?.LinkIds.Length ?? 0,
            snapshot?.TrialIds.Length ?? 0,
            snapshot?.ClosedInvestigationIds.Length ?? 0,
            blocked);
    }

    public async Task<UnmergeResult> UnmergeAsync(Guid duplicateId, string actor, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var preliminaryAudit = await db.ClientMergeAudits.AsNoTracking()
            .Where(a => a.DuplicateId == duplicateId && a.ReversedAt == null)
            .OrderByDescending(a => a.MergedAt)
            .FirstOrDefaultAsync(ct);
        if (preliminaryAudit is null)
        {
            var preliminaryDuplicate = await db.Clients.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == duplicateId, ct);
            var (preliminaryBlocked, _) = await ValidateUnmerge(
                db, preliminaryDuplicate, audit: null, ct: ct);
            return new UnmergeResult(false, preliminaryBlocked ?? "No live merge was found.");
        }

        var (firstFenceId, secondFenceId) = OrderedClientIds(duplicateId, preliminaryAudit.SurvivorId);
        await using var firstFence = await ClientMutationFence.AcquireAsync(db, firstFenceId, ct);
        await using var secondFence = await ClientMutationFence.AcquireAsync(db, secondFenceId, ct);
        await using var ownershipFence = await ClientMutationFence.AcquireMappingOwnershipAsync(db, ct);
        var now = clock.UtcNow;

        var duplicate = await db.Clients.FirstOrDefaultAsync(c => c.Id == duplicateId, ct);
        var audit = await LiveAudit(db, duplicateId, ct);

        // The live merge may have changed while this call waited for the two
        // preliminary client fences. Never proceed against a new survivor whose
        // fence we do not hold; the operator can refresh and retry instead.
        if (audit is null
            || audit.Id != preliminaryAudit.Id
            || audit.SurvivorId != preliminaryAudit.SurvivorId
            || duplicate?.MergedIntoClientId != audit.SurvivorId)
        {
            return new UnmergeResult(false,
                "This merge changed while the unmerge was waiting. Refresh and try again.");
        }

        var (blocked, _) = await ValidateUnmerge(db, duplicate, audit, ct);
        if (blocked is not null) return new UnmergeResult(false, blocked);

        var survivor = await db.Clients.FirstOrDefaultAsync(c => c.Id == audit!.SurvivorId, ct);
        if (survivor is null) return new UnmergeResult(false, "The surviving account no longer exists — can't unmerge.");

        var snap = Deserialize(audit!);
        if (await HasBillingRelevantConversionAsync(db, duplicateId, survivor.Id, ct))
            return new UnmergeResult(false, BillingRelevantConversionBlock);
        if (await HasPostMergeActivityAsync(db, duplicateId, survivor.Id, snap, ct))
            return new UnmergeResult(false, PostMergeActivityBlock);

        // 1. Move identity links back — only those still parented on the survivor
        //    (something merged/relinked later shouldn't be clobbered by this undo).
        var links = await db.IdentityLinks.Where(l => snap.LinkIds.Contains(l.Id) && l.ClientId == survivor.Id).ToListAsync(ct);
        foreach (var l in links) { l.ClientId = duplicateId; l.LinkVersion++; }

        // 2. Re-stamp the mutable projections back to the duplicate.
        foreach (var s in await db.StripeSubscriptions.Where(s => snap.SubscriptionIds.Contains(s.SubscriptionId) && s.ClientId == survivor.Id).ToListAsync(ct)) s.ClientId = duplicateId;
        foreach (var i in await db.StripeInvoices.Where(i => snap.InvoiceIds.Contains(i.InvoiceId) && i.ClientId == survivor.Id).ToListAsync(ct)) i.ClientId = duplicateId;

        // 3. Move child + enforcement rows back.
        foreach (var t in await db.TrialPeriods.Where(t => snap.TrialIds.Contains(t.Id) && t.ClientId == survivor.Id).ToListAsync(ct)) t.ClientId = duplicateId;
        foreach (var p in await db.PauseOperations.Where(p => snap.PauseIds.Contains(p.Id) && p.ClientId == survivor.Id).ToListAsync(ct)) p.ClientId = duplicateId;
        foreach (var d in await db.DunningCases.Where(d => snap.DunningIds.Contains(d.Id) && d.ClientId == survivor.Id).ToListAsync(ct)) d.ClientId = duplicateId;

        // 4. Reopen the investigation items this merge closed — but only if they're
        //    still in the exact state the merge left them (untouched since).
        foreach (var item in await db.InvestigationItems.Where(i => snap.ClosedInvestigationIds.Contains(i.Id)).ToListAsync(ct))
        {
            if (item.Status != InvestigationStatus.Resolved || item.ResolvedAt != audit!.MergedAt) continue;
            item.Status = InvestigationStatus.Open;
            item.ResolvedAt = null;
            item.ResolvedBy = null;
            item.ResolutionNote = null;
        }

        // 5. Unmerge changes both required-link sets. Any live verification on
        //    either side is now stale; pre-merge snapshots are never revived.
        var affectedClientIds = new[] { survivor.Id, duplicateId };
        foreach (var verification in await db.MappingVerifications
                     .Where(v => affectedClientIds.Contains(v.ClientId) && v.InvalidatedAt == null)
                     .ToListAsync(ct))
        {
            verification.InvalidatedAt = now;
        }

        // 6. Roll back the profile blanks the merge filled — but only where the
        //    survivor still holds the value the merge copied in (never clobber a
        //    later human edit).
        foreach (var field in snap.BackfilledFields)
        {
            switch (field)
            {
                case MergeSnapshot.FieldContactName when survivor.ContactName == duplicate!.ContactName: survivor.ContactName = null; break;
                case MergeSnapshot.FieldEmail when survivor.Email == duplicate!.Email: survivor.Email = null; break;
                case MergeSnapshot.FieldPhone when survivor.Phone == duplicate!.Phone: survivor.Phone = null; break;
                case MergeSnapshot.FieldCountry when survivor.Country == duplicate!.Country: survivor.Country = null; break;
                case MergeSnapshot.FieldPackage when survivor.PackageId == duplicate!.PackageId: survivor.PackageId = null; break;
                case MergeSnapshot.FieldArrangement
                    when survivor.ExpectedAmount == duplicate!.ExpectedAmount
                        && survivor.CadenceDays == duplicate.CadenceDays
                        && survivor.ArrangementStatus == duplicate.ArrangementStatus:
                    survivor.ExpectedAmount = null;
                    survivor.CadenceDays = null;
                    survivor.ArrangementStatus = ArrangementStatus.Unknown;
                    break;
            }
        }

        // 7. Strip the merge line from the survivor's notes, if still present verbatim.
        if (snap.NoteLineAppended is { } line && survivor.Notes is { } notes)
        {
            if (snap.NotesWasEmpty)
            {
                if (notes == line) survivor.Notes = null;
            }
            else if (notes.EndsWith("\n" + line, StringComparison.Ordinal))
            {
                survivor.Notes = notes[..^(line.Length + 1)];
            }
        }

        // 8. Un-retire the duplicate. Both accounts remain Shadow until an
        //    operator verifies their new, separate identity sets.
        duplicate!.MergedIntoClientId = null;
        duplicate.MergedAt = null;
        duplicate.EnforcementMode = EnforcementMode.Shadow;
        survivor.EnforcementMode = EnforcementMode.Shadow;

        await SupersedeNonterminalActionsAsync(
            db,
            affectedClientIds,
            "Superseded because unmerge changed the complete identity mapping; re-verify each account before staging new work.",
            ct);

        // 9. Stamp the audit as reversed — kept for history, never reused.
        audit!.ReversedAt = now;
        audit.ReversedBy = actor;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new UnmergeResult(false, "One of these accounts changed while unmerging — reload and try again.");
        }

        return new UnmergeResult(true,
            $"Unmerged \"{duplicate.BusinessName}\" from \"{survivor.BusinessName}\". {links.Count} link(s) moved back; both accounts remain Shadow until their separate mappings are verified.",
            duplicateId);
    }

    /// <summary>The live (un-reversed) audit for a retired duplicate, newest first.</summary>
    private static Task<ClientMergeAudit?> LiveAudit(RdDbContext db, Guid duplicateId, CancellationToken ct) =>
        db.ClientMergeAudits
            .Where(a => a.DuplicateId == duplicateId && a.ReversedAt == null)
            .OrderByDescending(a => a.MergedAt)
            .FirstOrDefaultAsync(ct);

    private MergeSnapshot Deserialize(ClientMergeAudit audit) =>
        JsonSerializer.Deserialize<MergeSnapshot>(audit.SnapshotJson, Json) ?? new MergeSnapshot();

    /// <summary>Returns (blockedReason, survivorName). blockedReason is null when the unmerge may proceed.</summary>
    private static async Task<(string? Blocked, string SurvivorName)> ValidateUnmerge(
        RdDbContext db, Client? duplicate, ClientMergeAudit? audit, CancellationToken ct)
    {
        if (duplicate is null) return ("That account no longer exists.", "(unknown)");
        if (duplicate.MergedIntoClientId is null) return ("This account isn't merged into anything.", "(unknown)");
        if (audit is null)
            return ("This account was merged before unmerge was supported, so there's no restore snapshot — it has to be unmerged manually.", "(unknown)");

        // Guard against reversing out of order: if the survivor has since absorbed
        // this account into a further merge, unwind that one first.
        if (await db.Clients.AnyAsync(c => c.Id == audit.SurvivorId && c.MergedIntoClientId != null, ct))
            return ("The surviving account was itself later merged elsewhere — unmerge that one first.", "(unknown)");

        var survivor = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == audit.SurvivorId, ct);
        if (survivor is null) return ("The surviving account no longer exists — can't unmerge.", "(unknown)");
        return (null, survivor.BusinessName);
    }

    /// <summary>Does this client already have other accounts merged into it?</summary>
    private static async Task<bool> AbsorbedOthers(RdDbContext db, Guid clientId, CancellationToken ct) =>
        await db.Clients.AnyAsync(c => c.MergedIntoClientId == clientId, ct);

    private static (Guid First, Guid Second) OrderedClientIds(Guid left, Guid right) =>
        left.CompareTo(right) <= 0 ? (left, right) : (right, left);

    private static Task<bool> HasBillingRelevantConversionAsync(
        RdDbContext db, Guid firstClientId, Guid secondClientId, CancellationToken ct) =>
        db.ConvertIntents.AnyAsync(
            intent => (intent.ClientId == firstClientId || intent.ClientId == secondClientId)
                      && (intent.State == ConvertIntentState.Drafted
                          || intent.State == ConvertIntentState.AwaitingPayment
                          || intent.State == ConvertIntentState.Paid
                          || intent.State == ConvertIntentState.Expired),
            ct);

    /// <summary>
    /// Once either side records new operational history, snapshot reversal can no
    /// longer prove which business originated append-only rows. Fail closed rather
    /// than split money or enforcement history across the restored accounts.
    /// </summary>
    private static async Task<bool> HasPostMergeActivityAsync(
        RdDbContext db,
        Guid duplicateId,
        Guid survivorId,
        MergeSnapshot snapshot,
        CancellationToken ct)
    {
        var ids = new[] { duplicateId, survivorId };
        return await db.LedgerEntries.AnyAsync(
                   row => ids.Contains(row.ClientId)
                          && !snapshot.KnownLedgerEntryIds.Contains(row.Id), ct)
               || await db.IdentityLinks.AnyAsync(
                   link => ids.Contains(link.ClientId)
                           && !snapshot.KnownIdentityLinkIds.Contains(link.Id), ct)
               || await db.ConvertIntents.AnyAsync(
                   intent => ids.Contains(intent.ClientId)
                             && !snapshot.KnownConvertIntentIds.Contains(intent.Id), ct)
               || await db.TrialPeriods.AnyAsync(
                   trial => ids.Contains(trial.ClientId)
                            && !snapshot.KnownTrialIds.Contains(trial.Id), ct)
               || await db.PauseOperations.AnyAsync(
                   pause => ids.Contains(pause.ClientId)
                            && !snapshot.KnownPauseIds.Contains(pause.Id), ct)
               || await db.DunningCases.AnyAsync(
                   dunning => ids.Contains(dunning.ClientId)
                              && !snapshot.KnownDunningIds.Contains(dunning.Id), ct)
               || await db.InvestigationItems.AnyAsync(
                   item => item.ClientId != null
                           && ids.Contains(item.ClientId.Value)
                           && !snapshot.KnownInvestigationIds.Contains(item.Id), ct)
               || await db.OutboxActions.AnyAsync(
                   action => ids.Contains(action.ClientId)
                             && !snapshot.KnownOutboxIds.Contains(action.Id), ct)
               || await db.StripeSubscriptions.AnyAsync(
                   subscription => subscription.ClientId != null
                                   && ids.Contains(subscription.ClientId.Value)
                                   && !snapshot.KnownStripeSubscriptionIds.Contains(
                                       subscription.SubscriptionId), ct)
               || await db.StripeInvoices.AnyAsync(
                   invoice => invoice.ClientId != null
                              && ids.Contains(invoice.ClientId.Value)
                              && !snapshot.KnownStripeInvoiceIds.Contains(invoice.InvoiceId), ct);
    }

    private static async Task SupersedeNonterminalActionsAsync(
        RdDbContext db,
        IReadOnlyCollection<Guid> clientIds,
        string reason,
        CancellationToken ct)
    {
        var actions = await db.OutboxActions
            .Where(action => clientIds.Contains(action.ClientId)
                             && (action.Status == OutboxStatus.Pending
                                 || action.Status == OutboxStatus.AwaitingApproval
                                 || action.Status == OutboxStatus.Approved
                                 || action.Status == OutboxStatus.Leased
                                 || action.Status == OutboxStatus.Failed))
            .ToListAsync(ct);
        foreach (var action in actions)
        {
            action.Status = OutboxStatus.Superseded;
            action.ActionVersion++;
            action.LeaseOwner = null;
            action.FencingToken = null;
            action.LeaseUntil = null;
            action.NextAttemptAt = null;
            action.LastError = reason;
        }
    }

    private static string? Validate(Guid survivorId, Guid duplicateId, Client? survivor, Client? duplicate, bool duplicateAbsorbedOthers)
    {
        if (survivorId == duplicateId) return "A client can't be merged into itself.";
        if (survivor is null) return "The surviving account no longer exists.";
        if (duplicate is null) return "The duplicate account no longer exists.";
        if (duplicate.MergedIntoClientId is not null) return "That duplicate was already merged into another account.";
        if (survivor.MergedIntoClientId is not null) return "The surviving account is itself merged into another — merge into that one instead.";
        if (duplicateAbsorbedOthers) return "This account has already absorbed other accounts; pick it as the survivor instead.";
        return null;
    }
}
