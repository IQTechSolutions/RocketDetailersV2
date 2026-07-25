using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using RD.Domain;
using RD.Domain.Entities;

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

    /// <summary>Blank survivor fields the merge filled from the duplicate (see the Field* tokens).</summary>
    public string[] BackfilledFields { get; init; } = [];

    /// <summary>The exact merge line appended to the survivor's notes, so unmerge can strip it back off.</summary>
    public string? NoteLineAppended { get; init; }

    /// <summary>Whether the survivor's notes were empty before the merge (so unmerge nulls them vs. trims the appended line).</summary>
    public bool NotesWasEmpty { get; init; }

    /// <summary>The duplicate's enforcement mode before it was set to Shadow on retirement — restored on unmerge.</summary>
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
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = clock.UtcNow;

        var survivor = await db.Clients.FirstOrDefaultAsync(c => c.Id == survivorId, ct);
        var duplicate = await db.Clients.FirstOrDefaultAsync(c => c.Id == duplicateId, ct);

        var blocked = Validate(survivorId, duplicateId, survivor, duplicate,
            await AbsorbedOthers(db, duplicateId, ct));
        if (blocked is not null) return new MergeResult(false, blocked);

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

        // 5. Invalidate the duplicate's mapping verifications — its links have moved,
        //    so any sign-off no longer describes reality.
        var invalidatedVerifications = await db.MappingVerifications.Where(v => v.ClientId == duplicateId && v.InvalidatedAt == null).ToListAsync(ct);
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
        var now = clock.UtcNow;

        var duplicate = await db.Clients.FirstOrDefaultAsync(c => c.Id == duplicateId, ct);
        var audit = await LiveAudit(db, duplicateId, ct);

        var (blocked, _) = await ValidateUnmerge(db, duplicate, audit, ct);
        if (blocked is not null) return new UnmergeResult(false, blocked);

        var survivor = await db.Clients.FirstOrDefaultAsync(c => c.Id == audit!.SurvivorId, ct);
        if (survivor is null) return new UnmergeResult(false, "The surviving account no longer exists — can't unmerge.");

        var snap = Deserialize(audit!);

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

        // 5. Un-invalidate the mapping verifications this merge invalidated (if untouched).
        foreach (var v in await db.MappingVerifications.Where(v => snap.InvalidatedVerificationIds.Contains(v.Id)).ToListAsync(ct))
            if (v.InvalidatedAt == audit!.MergedAt) v.InvalidatedAt = null;

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

        // 8. Un-retire the duplicate and restore its prior enforcement mode.
        duplicate!.MergedIntoClientId = null;
        duplicate.MergedAt = null;
        duplicate.EnforcementMode = snap.DuplicatePriorMode;

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
            $"Unmerged \"{duplicate.BusinessName}\" from \"{survivor.BusinessName}\". {links.Count} link(s) moved back; the account is live again.",
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
