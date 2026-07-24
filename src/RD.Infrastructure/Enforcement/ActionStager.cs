using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RD.Domain;
using RD.Domain.Entities;
using RD.Domain.Policy;

namespace RD.Infrastructure.Enforcement;

/// <summary>
/// Turns a policy verdict into staged <see cref="OutboxAction"/> rows for
/// Assist/Auto clients (Shadow never stages — its decisions are observation).
/// Assist stages AwaitingApproval; Auto stages Approved (the dispatcher still
/// revalidates before writing). Staging is idempotent: an open action of the
/// same natural key is never duplicated across heartbeats.
///
///   verdict ─┬─ Pause  ─▶ [PauseCampaign]                       (one action)
///            ├─ Resume ─▶ [ResumeCampaign]                      (one action)
///            └─ Dunn.  ─▶ ensure DunningCase + DunningAttempt,
///                         [WriteGhlField]→[TriggerGhlWorkflow]  (sequence group)
///
/// Enqueues to the tracked context; the caller owns SaveChanges.
/// </summary>
public sealed class ActionStager(IClock clock, IOptions<EnforcementOptions> enforcement)
{
    private readonly EnforcementOptions _enf = enforcement.Value;

    public async Task StageAsync(RdDbContext db, Client client, ClientState state, PolicyDecision verdict, long killSwitchEpoch, CancellationToken ct)
    {
        if (client.EnforcementMode is not (EnforcementMode.Assist or EnforcementMode.Auto)) return;
        var status = client.EnforcementMode == EnforcementMode.Assist ? OutboxStatus.AwaitingApproval : OutboxStatus.Approved;
        var now = clock.UtcNow;

        switch (verdict.Action)
        {
            case ProposedActionType.Pause:
            case ProposedActionType.Resume:
                await StageCampaignActionAsync(db, client, verdict, status, killSwitchEpoch, now, ct);
                break;
            case ProposedActionType.DunningStep:
                await StageDunningStepAsync(db, client, state, verdict, status, killSwitchEpoch, now, ct);
                break;
            // Escalate/Investigate/None never touch the outbox — they are human/observation only.
        }
    }

    private async Task StageCampaignActionAsync(
        RdDbContext db, Client client, PolicyDecision verdict, OutboxStatus status, long epoch, DateTimeOffset now, CancellationToken ct)
    {
        var type = verdict.Action == ProposedActionType.Pause ? OutboxActionType.PauseCampaign : OutboxActionType.ResumeCampaign;
        var ids = (verdict.TargetCampaignIds ?? []).OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (ids.Count == 0) return;

        var key = $"{type}:{client.Id}:{string.Join(',', ids)}";
        if (await OpenActionExistsAsync(db, key, ct)) return;

        db.OutboxActions.Add(new OutboxAction
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            DecisionId = Guid.Empty, // linked by the caller after the Decision row is created, if desired
            ActionType = type,
            PayloadJson = JsonSerializer.Serialize(new CampaignActionPayload(ids)),
            IdempotencyKey = key,
            Status = status,
            ExpectedKillSwitchEpoch = epoch,
            CreatedAt = now,
        });
    }

    private async Task StageDunningStepAsync(
        RdDbContext db, Client client, ClientState state, PolicyDecision verdict, OutboxStatus status, long epoch, DateTimeOffset now, CancellationToken ct)
    {
        var step = verdict.DunningStep ?? 1;

        // The dunning message must carry the hosted invoice URL that pays THE
        // failed invoice (never a Payment Link). No URL synced ⇒ we cannot dun
        // honestly; route to the work queue instead of faking a send.
        var invoiceUrl = await db.StripeInvoices.AsNoTracking()
            .Where(i => i.ClientId == client.Id && i.Status == "open" && i.HostedInvoiceUrl != null)
            .OrderBy(i => i.CreatedAtSource)
            .Select(i => i.HostedInvoiceUrl)
            .FirstOrDefaultAsync(ct);
        var contactId = await db.IdentityLinks.AsNoTracking()
            .Where(l => l.ClientId == client.Id && l.System == ExternalSystem.Ghl && l.Kind == LinkKind.Contact && l.InvalidatedAt == null)
            .Select(l => l.ExternalId)
            .FirstOrDefaultAsync(ct);

        if (invoiceUrl is null || contactId is null || string.IsNullOrEmpty(_enf.DunningWorkflowId))
        {
            await AddInvestigationOnceAsync(db, client.Id, InvestigationKind.DeliveryUnverified,
                invoiceUrl is null ? "Dunning step due but no hosted invoice URL is synced — cannot dun honestly."
                : contactId is null ? "Dunning step due but no GHL contact is linked."
                : "Dunning step due but no dunning workflow is configured (Enforcement:DunningWorkflowId).",
                now, ct);
            return;
        }

        // Ensure an open case, then the attempt for this step (idempotent).
        var dunningCase = await db.DunningCases
            .Include(c => c.Attempts)
            .Where(c => c.ClientId == client.Id && c.Status == DunningCaseStatus.Open)
            .OrderByDescending(c => c.OpenedAt)
            .FirstOrDefaultAsync(ct);
        if (dunningCase is null)
        {
            var invoiceId = await db.StripeInvoices.AsNoTracking()
                .Where(i => i.ClientId == client.Id && i.Status == "open")
                .OrderBy(i => i.CreatedAtSource).Select(i => i.InvoiceId).FirstOrDefaultAsync(ct) ?? $"unknown:{client.Id}";
            dunningCase = new DunningCase
            {
                Id = Guid.NewGuid(), ClientId = client.Id, InvoiceExternalId = invoiceId,
                OpenedAt = now, WindowExpiresAt = now + _enf.DunningWindow, Status = DunningCaseStatus.Open,
            };
            db.DunningCases.Add(dunningCase);
        }
        else if (dunningCase.Attempts.Any(a => a.Step == step))
        {
            return; // this step is already staged/in-flight — don't duplicate
        }

        db.DunningAttempts.Add(new DunningAttempt
        {
            Id = Guid.NewGuid(), DunningCaseId = dunningCase.Id, Step = step, DueAt = now,
            CadenceCalendarJson = JsonSerializer.Serialize(new { cadenceHours = _enf.DunningCadenceHours, note = "hours-based; working-hours calendar pending OQ6" }),
        });

        var location = _enf.DefaultDunningLocationId;
        var group = Guid.NewGuid();

        db.OutboxActions.Add(new OutboxAction
        {
            Id = Guid.NewGuid(), ClientId = client.Id, DecisionId = Guid.Empty,
            ActionType = OutboxActionType.WriteGhlField,
            PayloadJson = JsonSerializer.Serialize(new GhlFieldPayload(location, contactId, _enf.InvoiceUrlFieldKey, invoiceUrl, dunningCase.Id, step)),
            IdempotencyKey = $"dunning:{dunningCase.Id}:{step}:write",
            Status = status, SequenceGroup = group, SequenceNo = 0,
            ExpectedKillSwitchEpoch = epoch, CreatedAt = now,
        });
        db.OutboxActions.Add(new OutboxAction
        {
            Id = Guid.NewGuid(), ClientId = client.Id, DecisionId = Guid.Empty,
            ActionType = OutboxActionType.TriggerGhlWorkflow,
            PayloadJson = JsonSerializer.Serialize(new GhlWorkflowPayload(location, contactId, _enf.DunningWorkflowId, dunningCase.Id, step)),
            IdempotencyKey = $"dunning:{dunningCase.Id}:{step}:trigger",
            Status = status, SequenceGroup = group, SequenceNo = 1,
            ExpectedKillSwitchEpoch = epoch, CreatedAt = now,
        });
    }

    private static async Task<bool> OpenActionExistsAsync(RdDbContext db, string key, CancellationToken ct) =>
        await db.OutboxActions.AnyAsync(a => a.IdempotencyKey == key &&
            (a.Status == OutboxStatus.Pending || a.Status == OutboxStatus.AwaitingApproval
             || a.Status == OutboxStatus.Approved || a.Status == OutboxStatus.Leased), ct);

    private static async Task AddInvestigationOnceAsync(RdDbContext db, Guid clientId, InvestigationKind kind, string detail, DateTimeOffset now, CancellationToken ct)
    {
        var exists = await db.InvestigationItems.AnyAsync(
            i => i.ClientId == clientId && i.Kind == kind && i.Status == InvestigationStatus.Open, ct);
        if (!exists)
            db.InvestigationItems.Add(new InvestigationItem { Id = Guid.NewGuid(), ClientId = clientId, Kind = kind, Detail = detail, CreatedAt = now });
    }
}
