using RD.Domain;

namespace RD.Infrastructure.Enforcement;

/// <summary>
/// Pure assessment of whether an Assist-mode client has earned Auto. Two gates,
/// both from OV2 / the design:
///
///   1. A streak of consecutive CLEAN days since entering Assist, at least
///      `threshold` long. A day is clean when the engine needed no human
///      override for that client: no needs-investigation verdict (Investigate/
///      Escalate) and no dismissed action. A no-activity day is vacuously clean.
///   2. An EXERCISED failure path — the client has been through at least one
///      real (or test-clock-simulated) enforcement action that executed
///      end-to-end. 14 quiet days prove nothing if the machinery never fired
///      for this client.
///
/// Plus the standing preconditions: currently in Assist, mapping verified, kill
/// switch not engaged (the caller supplies these).
/// </summary>
public static class PromotionLadder
{
    public sealed record Assessment(
        int CleanDayStreak,
        int Threshold,
        bool ExercisedFailurePath,
        bool MappingVerified,
        bool InAssist,
        bool KillSwitchEngaged,
        bool CanPromote,
        IReadOnlyList<string> Blockers);

    /// <param name="uncleanDays">Dates (UTC) that had an override-worthy event for this client.</param>
    /// <param name="assistSince">When the client entered Assist (null ⇒ no Assist history yet).</param>
    public static Assessment Assess(
        EnforcementMode mode,
        DateTimeOffset? assistSince,
        IReadOnlyCollection<DateOnly> uncleanDays,
        bool exercised,
        bool mappingVerified,
        bool killSwitchEngaged,
        DateTimeOffset now,
        int threshold)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var streak = ConsecutiveCleanDays(assistSince, uncleanDays, today, threshold);

        var blockers = new List<string>();
        if (mode != EnforcementMode.Assist) blockers.Add($"Client is in {mode} mode; only Assist clients can be promoted to Auto.");
        if (!mappingVerified) blockers.Add("Mapping is not verified.");
        if (!exercised) blockers.Add("No enforcement action has been exercised for this client yet (needs one real or simulated dunning/pause cycle).");
        if (streak < threshold) blockers.Add($"Only {streak} of {threshold} consecutive clean days.");
        if (killSwitchEngaged) blockers.Add("The global kill switch is engaged.");

        return new Assessment(streak, threshold, exercised, mappingVerified,
            mode == EnforcementMode.Assist, killSwitchEngaged, blockers.Count == 0, blockers);
    }

    private static int ConsecutiveCleanDays(DateTimeOffset? assistSince, IReadOnlyCollection<DateOnly> uncleanDays, DateOnly today, int threshold)
    {
        if (assistSince is null) return 0;
        var assistDay = DateOnly.FromDateTime(assistSince.Value.UtcDateTime);
        var unclean = uncleanDays as HashSet<DateOnly> ?? [.. uncleanDays];

        var streak = 0;
        // Count back from today, but never before the day the client entered
        // Assist — days with no enforcement to be clean about don't count.
        for (var day = today; day >= assistDay && streak < threshold; day = day.AddDays(-1))
        {
            if (unclean.Contains(day)) break;
            streak++;
        }
        return streak;
    }
}
