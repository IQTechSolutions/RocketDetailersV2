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

/// <summary>Outcome of a merge — success flag plus a human sentence and the survivor to navigate to.</summary>
public sealed record MergeResult(bool Ok, string Message, Guid? SurvivorId = null);

/// <summary>
/// Merges a duplicate client record into a surviving one. The duplicate is a real
/// business whose Stripe/Meta/GHL records are LIVE — it is retired, never deleted,
/// so its third-party movement keeps being monitored.
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
/// Reversible by design: clear the pointer and move the links back.
/// </summary>
public sealed class ClientMergeService(IDbContextFactory<RdDbContext> factory, IClock clock)
{
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
        foreach (var s in await db.StripeSubscriptions.Where(s => s.ClientId == duplicateId).ToListAsync(ct)) s.ClientId = survivorId;
        foreach (var i in await db.StripeInvoices.Where(i => i.ClientId == duplicateId).ToListAsync(ct)) i.ClientId = survivorId;

        // 3. Move child + enforcement rows that key on ClientId.
        foreach (var t in await db.TrialPeriods.Where(t => t.ClientId == duplicateId).ToListAsync(ct)) t.ClientId = survivorId;
        foreach (var p in await db.PauseOperations.Where(p => p.ClientId == duplicateId).ToListAsync(ct)) p.ClientId = survivorId;
        foreach (var d in await db.DunningCases.Where(d => d.ClientId == duplicateId).ToListAsync(ct)) d.ClientId = survivorId;

        // 4. Close the duplicate's open investigation items — they were about the
        //    duplicate identity, which no longer stands alone.
        foreach (var item in await db.InvestigationItems.Where(i => i.ClientId == duplicateId && i.Status == InvestigationStatus.Open).ToListAsync(ct))
        {
            item.Status = InvestigationStatus.Resolved;
            item.ResolvedAt = now;
            item.ResolvedBy = actor;
            item.ResolutionNote = $"Resolved by merge into \"{survivor!.BusinessName}\".";
        }

        // 5. Invalidate the duplicate's mapping verifications — its links have moved,
        //    so any sign-off no longer describes reality.
        foreach (var v in await db.MappingVerifications.Where(v => v.ClientId == duplicateId && v.InvalidatedAt == null).ToListAsync(ct))
            v.InvalidatedAt = now;

        // 6. Survivor wins; fill only its blank profile fields from the duplicate.
        if (string.IsNullOrWhiteSpace(survivor!.ContactName)) survivor.ContactName = duplicate!.ContactName;
        if (string.IsNullOrWhiteSpace(survivor.Email)) survivor.Email = duplicate!.Email;
        if (string.IsNullOrWhiteSpace(survivor.Phone)) survivor.Phone = duplicate!.Phone;
        if (string.IsNullOrWhiteSpace(survivor.Country)) survivor.Country = duplicate!.Country;
        survivor.PackageId ??= duplicate!.PackageId;
        // Arrangement is a profile field too: adopt the duplicate's only if the
        // survivor has none inferred/confirmed yet.
        if (survivor.ExpectedAmount is null && survivor.ArrangementStatus == ArrangementStatus.Unknown && duplicate!.ExpectedAmount is not null)
        {
            survivor.ExpectedAmount = duplicate.ExpectedAmount;
            survivor.CadenceDays = duplicate.CadenceDays;
            survivor.ArrangementStatus = duplicate.ArrangementStatus;
        }
        survivor.Notes = string.IsNullOrWhiteSpace(survivor.Notes)
            ? $"Merged in \"{duplicate!.BusinessName}\" ({duplicateId}) on {now:yyyy-MM-dd} by {actor}."
            : survivor.Notes + $"\nMerged in \"{duplicate!.BusinessName}\" ({duplicateId}) on {now:yyyy-MM-dd} by {actor}.";

        // 7. Retire the duplicate: the pointer drops it from every active surface,
        //    and Shadow makes it inert even if something evaluates it directly.
        duplicate!.MergedIntoClientId = survivorId;
        duplicate.MergedAt = now;
        duplicate.EnforcementMode = EnforcementMode.Shadow;

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
