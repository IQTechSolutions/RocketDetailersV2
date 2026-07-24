using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace RD.Infrastructure.Gateways;

public interface IMetaAdsGateway
{
    /// <summary>All campaigns in the ad account (cursor-paginated sweep).</summary>
    Task<IReadOnlyList<MetaCampaignDto>> ListCampaignsAsync(string adAccountId, CancellationToken ct);

    /// <summary>Campaign-level daily insights (time_increment=1) for the inclusive date range.</summary>
    Task<IReadOnlyList<MetaInsightDto>> GetDailyInsightsAsync(
        string adAccountId, DateOnly since, DateOnly until, CancellationToken ct);

    /// <summary>Pause a campaign, then read back its status to confirm convergence. Blocked by the safety profile unless the target is the canary.</summary>
    Task<MetaWriteResult> PauseCampaignAsync(string campaignId, CancellationToken ct);

    /// <summary>Resume (set ACTIVE), then read back to confirm.</summary>
    Task<MetaWriteResult> ResumeCampaignAsync(string campaignId, CancellationToken ct);

    /// <summary>Current status without mutating — used for pause-provenance read-back and revalidation.</summary>
    Task<MetaCampaignDto?> GetCampaignAsync(string campaignId, CancellationToken ct);
}

/// <summary>The write's outcome plus the read-back evidence. Convergence is judged on the OBSERVED effective status, not the API's success flag.</summary>
public sealed record MetaWriteResult(
    string CampaignId,
    string RequestedStatus,
    string ObservedStatus,
    string ObservedEffectiveStatus,
    string? RemoteVersion,
    bool Converged);

/// <summary>Thrown when the safety profile refuses a Meta write (production writes disabled and the target is not the canary).</summary>
public sealed class MetaWriteBlockedException(string message) : InvalidOperationException(message);

/// <summary>DailyBudget is converted from Meta minor units (cents) to a major-unit decimal.</summary>
public sealed record MetaCampaignDto(
    string Id,
    string? Name,
    string Status,
    string EffectiveStatus,
    decimal? DailyBudget,
    string? UpdatedTime);

/// <summary>Spend is already a major-unit decimal string in the account currency (NOT minor units).</summary>
public sealed record MetaInsightDto(
    string CampaignId,
    DateOnly Date,
    decimal Spend,
    int Clicks,
    int LeadActions);

/// <summary>
/// Raw-HttpClient Meta Graph API reads. The access token travels only in the
/// Authorization header — never in the query string, never in logs.
/// </summary>
public sealed class MetaAdsGateway : IMetaAdsGateway
{
    private const int PageSize = 100;
    private const int MaxPages = 200;

    private readonly HttpClient _http;
    private readonly RetryHelper _retry;
    private readonly SafetyOptions _safety;

    public MetaAdsGateway(HttpClient http, IOptions<MetaOptions> options, IOptions<SafetyOptions> safety, RetryHelper retry)
    {
        _http = http;
        _retry = retry;
        _safety = safety.Value;
        var o = options.Value;
        _http.BaseAddress = new Uri(o.BaseUrl.TrimEnd('/') + "/");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", o.AccessToken);
    }

    public Task<MetaWriteResult> PauseCampaignAsync(string campaignId, CancellationToken ct)
        => SetStatusAsync(campaignId, "PAUSED", ct);

    public Task<MetaWriteResult> ResumeCampaignAsync(string campaignId, CancellationToken ct)
        => SetStatusAsync(campaignId, "ACTIVE", ct);

    private async Task<MetaWriteResult> SetStatusAsync(string campaignId, string desiredStatus, CancellationToken ct)
    {
        // Safety gate: production writes disabled ⇒ the ONLY writable target is the canary.
        if (!_safety.AllowProductionMetaWrites && campaignId != _safety.CanaryCampaignId)
            throw new MetaWriteBlockedException(
                $"Meta write to campaign {campaignId} refused: production Meta writes are disabled and this is not the canary campaign. " +
                "Set Safety:AllowProductionMetaWrites=true (after the M0 gates) to enable.");

        // Single POST — the outbox dispatcher owns retry; idempotency comes from read-back convergence,
        // not from repeating the mutation. Form body keeps the token in the header only.
        using var request = new HttpRequestMessage(HttpMethod.Post, Uri.EscapeDataString(campaignId))
        {
            Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("status", desiredStatus)]),
        };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await using (var _ = await response.Content.ReadAsStreamAsync(ct)) { }
        response.EnsureSuccessStatusCode();

        // Read-back: convergence is judged on what Meta actually reports now.
        var current = await GetCampaignAsync(campaignId, ct)
            ?? throw new HttpRequestException($"Campaign {campaignId} not found on read-back after {desiredStatus}.");

        var converged = desiredStatus == "PAUSED"
            ? current.EffectiveStatus is "PAUSED" or "CAMPAIGN_PAUSED"
            : current.EffectiveStatus is "ACTIVE";

        return new MetaWriteResult(campaignId, desiredStatus, current.Status, current.EffectiveStatus, current.UpdatedTime, converged);
    }

    public async Task<MetaCampaignDto?> GetCampaignAsync(string campaignId, CancellationToken ct)
    {
        var url = $"{Uri.EscapeDataString(campaignId)}?fields=name,status,effective_status,daily_budget,updated_time";
        using var response = await _retry.SendAsync(_http, () => new HttpRequestMessage(HttpMethod.Get, url), ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        var c = await GatewayHttp.ReadAsAsync<CampaignJson>(response, ct);
        if (c.Id.Length == 0) return null;
        return new MetaCampaignDto(
            c.Id, c.Name, c.Status ?? "", c.EffectiveStatus ?? "",
            ParseDecimal(c.DailyBudget) is { } cents ? cents / 100m : null, c.UpdatedTime);
    }

    public async Task<IReadOnlyList<MetaCampaignDto>> ListCampaignsAsync(string adAccountId, CancellationToken ct)
    {
        var baseUrl = $"{NormalizeActId(adAccountId)}/campaigns" +
                      $"?fields=name,status,effective_status,daily_budget,updated_time&limit={PageSize}";

        var rows = await SweepAsync<CampaignJson>(baseUrl, ct);
        return rows
            .Where(c => c.Id.Length > 0)
            .Select(c => new MetaCampaignDto(
                c.Id,
                c.Name,
                c.Status ?? "",
                c.EffectiveStatus ?? "",
                // daily_budget arrives as a minor-unit (cents) string.
                ParseDecimal(c.DailyBudget) is { } cents ? cents / 100m : null,
                c.UpdatedTime))
            .ToList();
    }

    public async Task<IReadOnlyList<MetaInsightDto>> GetDailyInsightsAsync(
        string adAccountId, DateOnly since, DateOnly until, CancellationToken ct)
    {
        var timeRange = Uri.EscapeDataString(
            $"{{\"since\":\"{since:yyyy-MM-dd}\",\"until\":\"{until:yyyy-MM-dd}\"}}");
        var baseUrl = $"{NormalizeActId(adAccountId)}/insights" +
                      $"?level=campaign&fields=campaign_id,spend,clicks,actions" +
                      $"&time_increment=1&time_range={timeRange}&limit={PageSize}";

        var rows = await SweepAsync<InsightJson>(baseUrl, ct);
        return rows
            .Where(r => !string.IsNullOrEmpty(r.CampaignId) && !string.IsNullOrEmpty(r.DateStart))
            .Select(r => new MetaInsightDto(
                r.CampaignId!,
                DateOnly.Parse(r.DateStart!, CultureInfo.InvariantCulture),
                // spend is a major-unit decimal string in the account currency.
                ParseDecimal(r.Spend) ?? 0m,
                (int)(ParseDecimal(r.Clicks) ?? 0m),
                CountLeadActions(r.Actions)))
            .ToList();
    }

    /// <summary>Follows cursor pagination (paging.next → re-request with after=cursor) so the token stays in headers.</summary>
    private async Task<List<T>> SweepAsync<T>(string baseUrl, CancellationToken ct)
    {
        var results = new List<T>();
        string? after = null;
        for (var page = 0; ; page++)
        {
            if (page >= MaxPages) throw new InvalidOperationException("Meta pagination exceeded the page cap.");

            var url = after is null ? baseUrl : $"{baseUrl}&after={Uri.EscapeDataString(after)}";
            using var response = await _retry.SendAsync(
                _http, () => new HttpRequestMessage(HttpMethod.Get, url), ct);
            var envelope = await GatewayHttp.ReadAsAsync<MetaListJson<T>>(response, ct);
            results.AddRange(envelope.Data);

            after = envelope.Paging?.Next is not null ? envelope.Paging?.Cursors?.After : null;
            if (string.IsNullOrEmpty(after)) return results;
        }
    }

    /// <summary>
    /// Heuristic lead count: sums every action whose action_type mentions
    /// "lead" (covers "lead", "leadgen_grouped", "onsite_conversion.lead_grouped",
    /// pixel lead variants). The exact action taxonomy gets confirmed against
    /// the existing daily-stats email semantics before M3+ repoints it.
    /// </summary>
    private static int CountLeadActions(List<ActionJson>? actions)
    {
        if (actions is null) return 0;
        var total = 0m;
        foreach (var action in actions)
        {
            if (action.ActionType?.Contains("lead", StringComparison.OrdinalIgnoreCase) == true)
                total += ParseDecimal(action.Value) ?? 0m;
        }
        return (int)total;
    }

    private static string NormalizeActId(string adAccountId) =>
        adAccountId.StartsWith("act_", StringComparison.Ordinal) ? adAccountId : $"act_{adAccountId}";

    private static decimal? ParseDecimal(string? value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;

    private sealed class MetaListJson<T>
    {
        [JsonPropertyName("data")] public List<T> Data { get; set; } = [];
        [JsonPropertyName("paging")] public PagingJson? Paging { get; set; }
    }

    private sealed class PagingJson
    {
        [JsonPropertyName("cursors")] public CursorsJson? Cursors { get; set; }
        [JsonPropertyName("next")] public string? Next { get; set; }
    }

    private sealed class CursorsJson
    {
        [JsonPropertyName("after")] public string? After { get; set; }
    }

    private sealed class CampaignJson
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("effective_status")] public string? EffectiveStatus { get; set; }
        [JsonPropertyName("daily_budget")] public string? DailyBudget { get; set; }
        [JsonPropertyName("updated_time")] public string? UpdatedTime { get; set; }
    }

    private sealed class InsightJson
    {
        [JsonPropertyName("campaign_id")] public string? CampaignId { get; set; }
        [JsonPropertyName("spend")] public string? Spend { get; set; }
        [JsonPropertyName("clicks")] public string? Clicks { get; set; }
        [JsonPropertyName("actions")] public List<ActionJson>? Actions { get; set; }
        [JsonPropertyName("date_start")] public string? DateStart { get; set; }
        [JsonPropertyName("date_stop")] public string? DateStop { get; set; }
    }

    private sealed class ActionJson
    {
        [JsonPropertyName("action_type")] public string? ActionType { get; set; }
        [JsonPropertyName("value")] public string? Value { get; set; }
    }
}
