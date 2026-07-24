using RD.Domain;

namespace RD.Web.Services;

/// <summary>
/// Human-readable labels and help text for every enum the UI shows (Req 16:
/// intuitive UX, human-readable labels, help everywhere). Nothing renders a
/// raw enum name to the operator.
/// </summary>
public static class Human
{
    // ---------------- Investigation kinds ----------------

    public static string Title(this InvestigationKind kind) => kind switch
    {
        InvestigationKind.UnmappedIdentity => "no Stripe subscription linked",
        InvestigationKind.DuplicateStripeCustomer => "duplicate Stripe customers",
        InvestigationKind.ExternallyPausedPayment => "payment arrived for externally-paused ads",
        InvestigationKind.CanceledSubPayment => "payment received on a canceled subscription",
        InvestigationKind.NonUsdCurrency => "billed in a non-USD currency",
        InvestigationKind.MissingTrialExpiry => "trial has no expiry date",
        InvestigationKind.StaleSync => "sync data went stale",
        InvestigationKind.DeliveryUnverified => "dunning message delivery unverified",
        InvestigationKind.ExposureCapExceeded => "exposure cap exceeded",
        InvestigationKind.SpendAnomaly => "ad spend looks wrong",
        InvestigationKind.ImportConflict => "import conflict",
        _ => "other — read the detail",
    };

    public static string WhyItMatters(this InvestigationKind kind) => kind switch
    {
        InvestigationKind.UnmappedIdentity => "Enforcement impossible — billing can't drive ads until the subscription is linked.",
        InvestigationKind.DuplicateStripeCustomer => "Risk of double billing the same business.",
        InvestigationKind.ExternallyPausedPayment => "A human paused these ads; the app never auto-resumes what it didn't pause.",
        InvestigationKind.CanceledSubPayment => "A payment can't reactivate a canceled subscription — needs a re-subscribe.",
        InvestigationKind.NonUsdCurrency => "v1 is USD-only; these clients are excluded from the company exposure number.",
        InvestigationKind.MissingTrialExpiry => "A trial without an end date is a gap, not a free pass — backfill the real date.",
        InvestigationKind.StaleSync => "Decisions on stale data are dangerous; the freshness gate blocked them.",
        InvestigationKind.DeliveryUnverified => "The dunning clock only advances on verified sends — a client is never final-failed off phantom warnings.",
        InvestigationKind.ExposureCapExceeded => "This client's unbilled ad spend passed the max-loss cap — human decision required.",
        InvestigationKind.SpendAnomaly => "Spend far off the package budget, or zero spend while live.",
        InvestigationKind.ImportConflict => "Two clients claimed the same external identity during the spreadsheet import.",
        _ => "Doesn't fit the other buckets — read the item detail.",
    };

    /// <summary>The counting noun for a gap group ("45 clients — …" vs "19 conflicts — …").</summary>
    public static string UnitNoun(this InvestigationKind kind, int count) => kind switch
    {
        InvestigationKind.ImportConflict => count == 1 ? "conflict" : "conflicts",
        InvestigationKind.StaleSync or InvestigationKind.Other => count == 1 ? "item" : "items",
        _ => count == 1 ? "client" : "clients",
    };

    // ---------------- Enforcement ladder ----------------

    public static string Description(this EnforcementMode mode) => mode switch
    {
        EnforcementMode.Shadow => "Shadow — the engine observes and logs what it would do. No external writes, ever.",
        EnforcementMode.Assist => "Assist — the engine proposes; a human clicks Approve before anything touches Stripe, Meta or GHL.",
        EnforcementMode.Auto => "Auto — the engine acts on its own and reports to Slack. Earned after 14 clean days and one exercised failure path.",
        _ => mode.ToString(),
    };

    public const string LadderExplanation =
        "The enforcement ladder: Shadow → Assist → Auto. Each client climbs as their identity mapping is verified "
        + "and clean days accumulate; mapping drift demotes back to Shadow automatically, and the global kill switch reverts everyone.";

    // ---------------- Outbox actions ----------------

    public static string Label(this OutboxActionType type) => type switch
    {
        OutboxActionType.PauseCampaign => "Pause ads",
        OutboxActionType.ResumeCampaign => "Resume ads",
        OutboxActionType.WriteGhlField => "Write GHL field",
        OutboxActionType.TriggerGhlWorkflow => "Send dunning message (GHL)",
        OutboxActionType.VerifyGhlDelivery => "Verify message delivery",
        OutboxActionType.SendSlackAlert => "Send Slack alert",
        OutboxActionType.SendFallbackEmail => "Send fallback email",
        _ => type.ToString(),
    };

    public static string Icon(this OutboxActionType type) => type switch
    {
        OutboxActionType.PauseCampaign => MudBlazor.Icons.Material.Filled.PauseCircle,
        OutboxActionType.ResumeCampaign => MudBlazor.Icons.Material.Filled.PlayCircle,
        OutboxActionType.WriteGhlField => MudBlazor.Icons.Material.Filled.EditNote,
        OutboxActionType.TriggerGhlWorkflow => MudBlazor.Icons.Material.Filled.Sms,
        OutboxActionType.VerifyGhlDelivery => MudBlazor.Icons.Material.Filled.MarkChatRead,
        OutboxActionType.SendSlackAlert => MudBlazor.Icons.Material.Filled.NotificationsActive,
        OutboxActionType.SendFallbackEmail => MudBlazor.Icons.Material.Filled.Email,
        _ => MudBlazor.Icons.Material.Filled.Bolt,
    };

    // ---------------- Ledger ----------------

    public static string Label(this LedgerEntryType type) => type switch
    {
        LedgerEntryType.ChargePaid => "Charge paid",
        LedgerEntryType.ChargeFailed => "Charge failed",
        LedgerEntryType.Refund => "Refund",
        LedgerEntryType.Dispute => "Dispute",
        LedgerEntryType.AdSpend => "Ad spend",
        LedgerEntryType.Adjustment => "Adjustment",
        _ => type.ToString(),
    };

    // ---------------- Proposed actions (analytics / enforcement activity) ----------------

    public static string Label(this ProposedActionType action) => action switch
    {
        ProposedActionType.None => "No action (paid up)",
        ProposedActionType.Pause => "Would pause ads",
        ProposedActionType.Resume => "Would resume ads",
        ProposedActionType.DunningStep => "Would send dunning",
        ProposedActionType.Escalate => "Would escalate",
        ProposedActionType.Investigate => "Needs investigation",
        _ => action.ToString(),
    };

    // ---------------- Client segmentation (analytics client mix) ----------------

    public static string Label(this AccountType type) => type switch
    {
        AccountType.Master => "Master account",
        AccountType.Own => "Own account",
        _ => type.ToString(),
    };

    public static string Label(this ContractType type) => type switch
    {
        ContractType.Trial => "Trial",
        ContractType.Paid => "Paid",
        _ => type.ToString(),
    };

    // ---------------- Systems ----------------

    public static string Label(this ExternalSystem system) => system switch
    {
        ExternalSystem.Stripe => "Stripe",
        ExternalSystem.Meta => "Meta Ads",
        ExternalSystem.Ghl => "GoHighLevel",
        ExternalSystem.ClickUp => "ClickUp",
        _ => system.ToString(),
    };

    public static string Label(this LinkKind kind) => kind switch
    {
        LinkKind.Customer => "Customer",
        LinkKind.Subscription => "Subscription",
        LinkKind.Campaign => "Campaign",
        LinkKind.Contact => "Contact",
        LinkKind.Task => "Task",
        LinkKind.AdAccount => "Ad account",
        _ => kind.ToString(),
    };

    // ---------------- Money ----------------

    /// <summary>Whole-figure money for KPIs ("$4,318" / "CAD 1,204").</summary>
    public static string MoneyKpi(decimal amount, string currency)
        => currency == "USD" ? $"${amount:N0}" : $"{currency} {amount:N0}";

    /// <summary>Signed whole-figure money for a headline that can go negative ("+$4,318" / "−$1,204" / "$0").</summary>
    public static string MoneyKpiSigned(decimal amount, string currency)
    {
        var abs = Math.Abs(amount);
        var figure = currency == "USD" ? $"${abs:N0}" : $"{currency} {abs:N0}";
        var sign = amount < 0 ? "−" : amount > 0 ? "+" : "";
        return $"{sign}{figure}";
    }

    /// <summary>Exact money for ledger lines ("+$25.00" / "−$23.10", "CAD 25.00").</summary>
    public static string MoneySigned(decimal signedAmount, string currency)
    {
        var abs = Math.Abs(signedAmount);
        var figure = currency == "USD" ? $"${abs:N2}" : $"{currency} {abs:N2}";
        return signedAmount < 0 ? $"−{figure}" : $"+{figure}";
    }

    /// <summary>"3 min ago" / "2 h ago" / "5 days ago" for timestamps.</summary>
    public static string Ago(DateTimeOffset then, DateTimeOffset now)
    {
        var span = now - then;
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        return span.TotalMinutes < 1 ? "just now"
            : span.TotalMinutes < 60 ? $"{(int)span.TotalMinutes} min ago"
            : span.TotalHours < 24 ? $"{(int)span.TotalHours} h ago"
            : (int)span.TotalDays == 1 ? "1 day ago"
            : $"{(int)span.TotalDays} days ago";
    }
}
