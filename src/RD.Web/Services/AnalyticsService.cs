using Microsoft.EntityFrameworkCore;
using RD.Domain;
using RD.Infrastructure;

namespace RD.Web.Services;

/// <summary>
/// Loads owner-analytics inputs through the context factory (Blazor circuits
/// are long-lived — never inject RdDbContext directly) and hands them to the
/// pure <see cref="AnalyticsRules"/> for derivation. Read-only: writes nothing.
///
/// Register in the host: <c>builder.Services.AddScoped&lt;AnalyticsService&gt;();</c>
/// </summary>
public class AnalyticsService(IDbContextFactory<RdDbContext> factory, IClock clock)
{
    /// <summary>Enforcement-activity window the dashboard requests (last 30 days).</summary>
    public const int WindowDays = AnalyticsRules.DefaultWindowDays;

    public async Task<AnalyticsSnapshot> GetSnapshotAsync(CancellationToken ct = default)
        => AnalyticsRules.Compute(await LoadAsync(ct), clock.UtcNow, WindowDays);

    public async Task<AnalyticsData> LoadAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var masters = db.Clients.Where(c => c.AccountType == AccountType.Master);

        // Per master-client metadata (name, currency, mode, package name via LEFT JOIN).
        var masterClients = await masters
            .Select(c => new MasterClientFact(
                c.Id, c.BusinessName, c.CurrencyCode, c.EnforcementMode,
                c.Package != null ? c.Package.Name : null))
            .ToListAsync(ct);

        // ChargePaid + AdSpend rows for master clients, tagged with the client's currency.
        // Rolled up in memory by AnalyticsRules (v1: hundreds of clients — same pattern as the cockpit).
        var ledger = await db.LedgerEntries
            .Where(l => l.Type == LedgerEntryType.ChargePaid || l.Type == LedgerEntryType.AdSpend)
            .Join(masters, l => l.ClientId, c => c.Id,
                (l, c) => new LedgerFact(l.ClientId, c.CurrencyCode, l.Type, l.SignedAmount, l.OccurredAt))
            .ToListAsync(ct);

        // Verified-mapping count: master clients with a current (non-invalidated) verification.
        var verifiedCount = await db.MappingVerifications
            .Where(v => v.InvalidatedAt == null)
            .Join(masters, v => v.ClientId, c => c.Id, (v, c) => v.ClientId)
            .Distinct()
            .CountAsync(ct);

        // Decisions grouped by UTC day + proposed action over the window (grouped in SQL).
        // Timestamps are stored UTC (+00:00), so DATEPART y/m/d == the UTC calendar day.
        var windowStart = new DateTimeOffset(
            clock.UtcNow.UtcDateTime.Date.AddDays(-(WindowDays - 1)), TimeSpan.Zero);
        var decisionGroups = await db.Decisions
            .Where(d => d.EvaluatedAt >= windowStart)
            .GroupBy(d => new { d.EvaluatedAt.Year, d.EvaluatedAt.Month, d.EvaluatedAt.Day, d.ProposedAction })
            .Select(g => new { g.Key.Year, g.Key.Month, g.Key.Day, g.Key.ProposedAction, Count = g.Count() })
            .ToListAsync(ct);
        var decisionsByDay = decisionGroups
            .Select(g => new DecisionDayFact(new DateOnly(g.Year, g.Month, g.Day), g.ProposedAction, g.Count))
            .ToList();

        // Open investigation items by kind (mirrors the cockpit's mapping-gap panel).
        var gaps = await db.InvestigationItems
            .Where(i => i.Status == InvestigationStatus.Open)
            .GroupBy(i => i.Kind)
            .Select(g => new MappingGap(g.Key, g.Count()))
            .ToListAsync(ct);

        // Client mix: AccountType × ContractType over ALL clients.
        var segments = await db.Clients
            .GroupBy(c => new { c.AccountType, c.ContractType })
            .Select(g => new ClientSegmentFact(g.Key.AccountType, g.Key.ContractType, g.Count()))
            .ToListAsync(ct);

        // Enforcement-ladder mix over master clients only (the policy loop's population).
        var modes = await masters
            .GroupBy(c => c.EnforcementMode)
            .Select(g => new ModeFact(g.Key, g.Count()))
            .ToListAsync(ct);

        return new AnalyticsData
        {
            MasterClientCount = masterClients.Count,
            VerifiedMasterClientCount = verifiedCount,
            MasterClients = masterClients,
            MasterClientLedger = ledger,
            DecisionsByDay = decisionsByDay,
            OpenInvestigations = gaps,
            ClientSegments = segments,
            MasterModes = modes,
        };
    }
}
