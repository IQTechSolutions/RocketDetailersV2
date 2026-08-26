using Microsoft.EntityFrameworkCore;
using RD.Domain;
using RD.Domain.Entities;
using RD.Domain.Policy;

namespace RD.Infrastructure.Sync;

/// <summary>
/// Maintains one open Shadow prediction incident per exact campaign/action and
/// target state. It only mutates V2 audit rows; it has no provider dependency.
/// </summary>
public sealed class MetaShadowPredictionRecorder
{
    public async Task RecordAsync(
        RdDbContext db,
        Client client,
        ClientState state,
        PolicyDecision verdict,
        Guid decisionId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var candidates = BuildCandidates(client, state, verdict);
        var candidateByKey = candidates.ToDictionary(candidate => candidate.Key);
        var active = await db.MetaShadowPredictions
            .Where(prediction => prediction.ClientId == client.Id && prediction.EndedAt == null)
            .ToListAsync(ct);

        foreach (var existing in active)
        {
            var key = PredictionKey.From(existing);
            if (!candidateByKey.ContainsKey(key)) existing.EndedAt = now;
        }

        var activeKeys = active
            .Where(prediction => prediction.EndedAt == null)
            .Select(PredictionKey.From)
            .ToHashSet();
        foreach (var candidate in candidates.Where(candidate => !activeKeys.Contains(candidate.Key)))
        {
            db.MetaShadowPredictions.Add(new MetaShadowPrediction
            {
                Id = Guid.NewGuid(),
                ClientId = client.Id,
                DecisionId = decisionId,
                CampaignId = candidate.Key.CampaignId,
                ProposedAction = candidate.Key.Action,
                DesiredStatus = candidate.DesiredStatus,
                TargetState = candidate.Key.TargetState,
                StartedAt = now,
            });
        }
    }

    private static IReadOnlyList<PredictionCandidate> BuildCandidates(
        Client client,
        ClientState state,
        PolicyDecision verdict)
    {
        if (client.EnforcementMode != EnforcementMode.Shadow
            || verdict.Action is not (ProposedActionType.Pause or ProposedActionType.Resume))
            return [];

        var desiredStatus = verdict.Action == ProposedActionType.Pause
            ? MetaShadowComparison.PausedStatus
            : MetaShadowComparison.ActiveStatus;
        var targetIds = (verdict.TargetCampaignIds ?? [])
            .ToHashSet(StringComparer.Ordinal);
        var seenCampaignIds = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new List<PredictionCandidate>();

        foreach (var campaign in state.Campaigns)
        {
            seenCampaignIds.Add(campaign.CampaignId);
            var targetState = targetIds.Contains(campaign.CampaignId)
                ? MetaShadowTargetState.Executable
                : IsDesiredState(campaign, desiredStatus)
                    ? MetaShadowTargetState.AlreadySatisfied
                    : MetaShadowTargetState.Unjudgeable;
            candidates.Add(new PredictionCandidate(
                new PredictionKey(campaign.CampaignId, verdict.Action, targetState),
                desiredStatus));
        }

        foreach (var missingTargetId in targetIds.Where(id => !seenCampaignIds.Contains(id)))
        {
            candidates.Add(new PredictionCandidate(
                new PredictionKey(missingTargetId, verdict.Action, MetaShadowTargetState.Unjudgeable),
                desiredStatus));
        }

        if (candidates.Count == 0)
        {
            candidates.Add(new PredictionCandidate(
                new PredictionKey(null, verdict.Action, MetaShadowTargetState.NoActiveTarget),
                desiredStatus));
        }

        return candidates;
    }

    private static bool IsDesiredState(CampaignState campaign, string desiredStatus) =>
        desiredStatus == MetaShadowComparison.PausedStatus
            ? campaign.IsPaused
            : campaign.IsActive;

    private readonly record struct PredictionKey(
        string? CampaignId,
        ProposedActionType Action,
        MetaShadowTargetState TargetState)
    {
        public static PredictionKey From(MetaShadowPrediction prediction) =>
            new(prediction.CampaignId, prediction.ProposedAction, prediction.TargetState);
    }

    private sealed record PredictionCandidate(PredictionKey Key, string DesiredStatus);
}
