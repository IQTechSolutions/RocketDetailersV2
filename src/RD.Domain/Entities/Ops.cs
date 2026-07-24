namespace RD.Domain.Entities;

/// <summary>Recoverable webhook inbox — dedup insert and state mutation in one transaction; poison events visible, never lost.</summary>
public class WebhookInboxItem
{
    public Guid Id { get; set; }
    public ExternalSystem System { get; set; }
    public required string ExternalEventId { get; set; }
    public required string EventType { get; set; }
    public string? EntityRef { get; set; }
    public required string PayloadJson { get; set; }
    public WebhookStatus Status { get; set; } = WebhookStatus.Received;
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
}

/// <summary>
/// One row per sync sweep. Success is recorded only after a COMPLETE sweep —
/// a partially-paginated sync never looks green. Per-client freshness comes
/// from projection rows' SourceSyncedAt, not from this table alone.
/// </summary>
public class SyncRun
{
    public Guid Id { get; set; }
    public ExternalSystem System { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public SyncRunStatus Status { get; set; } = SyncRunStatus.Running;
    public string? Cursor { get; set; }
    public int ItemsSeen { get; set; }
    public string? Error { get; set; }
}

/// <summary>Singleton (enforced by unique constant key). Epoch bumps atomically; executors re-check before every external write.</summary>
public class KillSwitchState
{
    public int Id { get; set; } // always 1
    public bool Enabled { get; set; }
    public long Epoch { get; set; }
    public string? Actor { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset ChangedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>Alert fallback path: failed Slack posts land here and retry via SMTP — the alerting channel itself must not fail silent.</summary>
public class AlertLogEntry
{
    public Guid Id { get; set; }
    public required string Severity { get; set; }
    public required string Message { get; set; }
    public bool SlackDelivered { get; set; }
    public bool EmailDelivered { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
}
