namespace RD.Infrastructure.Enforcement;

/// <summary>
/// Enforcement timing and safety knobs. Cadence is a simple hours-based
/// schedule for M2; the working-hours/timezone refinement is OQ6 (still open)
/// and slots in here without touching the policy or the dispatcher.
/// </summary>
public sealed class EnforcementOptions
{
    public const string SectionName = "Enforcement";

    /// <summary>Hours between dunning steps. (Working-hours calendar pending OQ6.)</summary>
    public double DunningCadenceHours { get; set; } = 3;

    /// <summary>Total dunning window before final failure (design: 2 working days).</summary>
    public double DunningWindowHours { get; set; } = 48;

    /// <summary>The GHL workflow that renders the dunning message. Per-location if needed; single id for M2.</summary>
    public string DunningWorkflowId { get; set; } = "";

    /// <summary>The GHL custom-field key the dunning workflow reads for the hosted invoice URL.</summary>
    public string InvoiceUrlFieldKey { get; set; } = "hosted_invoice_url";

    /// <summary>
    /// Best-effort GHL location for dunning payloads until each contact's real
    /// location is resolved (per-contact location isn't persisted yet — see
    /// OQ: dunning location resolution). Moot under Safety:GhlTestMode, which
    /// redirects every send to the test contact regardless.
    /// </summary>
    public string DefaultDunningLocationId { get; set; } = "";

    /// <summary>Outbox lease duration — a crashed dispatcher's actions become re-claimable after this.</summary>
    public double LeaseSeconds { get; set; } = 120;

    /// <summary>How many actions one dispatcher pass claims.</summary>
    public int DispatchBatchSize { get; set; } = 20;

    /// <summary>Attempts before an action is dead-lettered for human attention.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Delay before verifying a dunning send actually reached the client (F2).</summary>
    public double DeliveryVerificationDelayMinutes { get; set; } = 10;

    /// <summary>Consecutive clean days in Assist required before a client is eligible for Auto (design + OV2).</summary>
    public int CleanDaysForAuto { get; set; } = 14;

    public TimeSpan DunningCadence => TimeSpan.FromHours(DunningCadenceHours);
    public TimeSpan DunningWindow => TimeSpan.FromHours(DunningWindowHours);
    public TimeSpan Lease => TimeSpan.FromSeconds(LeaseSeconds);
    public TimeSpan DeliveryVerificationDelay => TimeSpan.FromMinutes(DeliveryVerificationDelayMinutes);
}
