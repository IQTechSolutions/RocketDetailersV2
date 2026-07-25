namespace RD.Domain.Entities;

/// <summary>
/// One row per merge, capturing exactly what the merge moved and changed so it can
/// be reversed deterministically. Merges are rare and operator-driven; once a
/// duplicate's links, trials and rows are re-parented onto the survivor they carry
/// no origin marker, so without this record an unmerge would have to guess which of
/// the survivor's rows were originally the duplicate's. The snapshot removes the
/// guesswork.
///
/// The payload lives in <see cref="SnapshotJson"/> (a serialized MergeSnapshot) —
/// the moved ids plus the few prior values (enforcement mode, appended note) needed
/// to put things back. The row is retained after reversal (with
/// <see cref="ReversedAt"/> stamped) as an audit trail; it is never deleted.
/// </summary>
public class ClientMergeAudit
{
    public Guid Id { get; set; }

    /// <summary>The account that absorbed the duplicate.</summary>
    public Guid SurvivorId { get; set; }

    /// <summary>The account that was retired into the survivor.</summary>
    public Guid DuplicateId { get; set; }

    public required string MergedBy { get; set; }
    public DateTimeOffset MergedAt { get; set; }

    /// <summary>Serialized MergeSnapshot — the ids and prior values needed to reverse this merge.</summary>
    public required string SnapshotJson { get; set; }

    /// <summary>Set when the merge is undone. A reversed audit is inert but kept for history.</summary>
    public DateTimeOffset? ReversedAt { get; set; }
    public string? ReversedBy { get; set; }
}
