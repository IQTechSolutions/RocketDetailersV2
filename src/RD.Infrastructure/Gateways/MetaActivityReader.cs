using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace RD.Infrastructure.Gateways;

/// <summary>
/// Read-only boundary for Meta's ad-account activity audit trail. Keeping this
/// separate from IMetaAdsGateway makes write methods unavailable to the shadow
/// comparison workflow by construction.
/// </summary>
public interface IMetaActivityReader
{
    Task<IReadOnlyList<MetaActivityDto>> ListCampaignStatusActivitiesAsync(
        string adAccountId,
        DateTimeOffset since,
        DateTimeOffset until,
        CancellationToken ct);
}

public sealed record MetaActivityDto(
    DateTimeOffset EventTime,
    string EventType,
    string ObjectId,
    string? ObjectName,
    string? ObjectType,
    string? ActorId,
    string? ActorName,
    string? ApplicationId,
    string? ApplicationName,
    string? Tool,
    string? TranslatedEventType,
    string? OldStatus,
    string? NewStatus,
    string? ExtraDataJson);

/// <summary>
/// GET-only Meta activity reader. It asks for STATUS activity, retains only
/// campaign run-status events, and follows cursors without trusting paging.next
/// (which can echo credentials into a URL).
/// </summary>
public sealed class MetaActivityReader : IMetaActivityReader
{
    private const int PageSize = 100;
    private const int MaxPages = 200;
    private const string CampaignStatusEvent = "update_campaign_run_status";

    private readonly HttpClient _http;
    private readonly RetryHelper _retry;

    public MetaActivityReader(HttpClient http, IOptions<MetaOptions> options, RetryHelper retry)
    {
        _http = http;
        _retry = retry;
        var o = options.Value;
        _http.BaseAddress = new Uri(o.BaseUrl.TrimEnd('/') + "/");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", o.AccessToken);
    }

    public async Task<IReadOnlyList<MetaActivityDto>> ListCampaignStatusActivitiesAsync(
        string adAccountId,
        DateTimeOffset since,
        DateTimeOffset until,
        CancellationToken ct)
    {
        if (until < since) throw new ArgumentOutOfRangeException(nameof(until), "Until must not precede Since.");

        const string fields =
            "actor_id,actor_name,application_id,application_name,date_time_in_timezone," +
            "event_time,event_type,extra_data,object_id,object_name,object_type,tool,translated_event_type";
        var baseUrl = $"{NormalizeActId(adAccountId)}/activities" +
                      $"?fields={fields}&category=STATUS&add_children=true" +
                      $"&since={since.ToUnixTimeSeconds()}&until={until.ToUnixTimeSeconds()}&limit={PageSize}";

        var rows = await SweepAsync(baseUrl, ct);
        return rows
            .Where(row => string.Equals(row.EventType, CampaignStatusEvent, StringComparison.Ordinal)
                          && !string.IsNullOrWhiteSpace(row.ObjectId))
            .Select(ToDto)
            .ToList();
    }

    private async Task<List<ActivityJson>> SweepAsync(string baseUrl, CancellationToken ct)
    {
        var results = new List<ActivityJson>();
        string? after = null;
        for (var page = 0; ; page++)
        {
            if (page >= MaxPages) throw new InvalidOperationException("Meta activity pagination exceeded the page cap.");

            var url = after is null ? baseUrl : $"{baseUrl}&after={Uri.EscapeDataString(after)}";
            using var response = await _retry.SendAsync(
                _http,
                () => new HttpRequestMessage(HttpMethod.Get, url),
                ct);
            var envelope = await GatewayHttp.ReadAsAsync<ActivityEnvelopeJson>(response, ct);
            results.AddRange(envelope.Data);

            after = envelope.Paging?.Next is not null ? envelope.Paging.Cursors?.After : null;
            if (string.IsNullOrEmpty(after)) return results;
        }
    }

    private static MetaActivityDto ToDto(ActivityJson row)
    {
        var extraData = ExtraDataText(row.ExtraData);
        return new MetaActivityDto(
            ParseEventTime(row.EventTime),
            row.EventType ?? "",
            row.ObjectId ?? "",
            row.ObjectName,
            row.ObjectType,
            row.ActorId,
            row.ActorName,
            row.ApplicationId,
            row.ApplicationName,
            row.Tool,
            row.TranslatedEventType,
            ExtractStatus(extraData, "old_value"),
            ExtractStatus(extraData, "new_value"),
            extraData);
    }

    private static DateTimeOffset ParseEventTime(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var seconds))
            return DateTimeOffset.FromUnixTimeSeconds(seconds);

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds))
                return DateTimeOffset.FromUnixTimeSeconds(seconds);
            if (DateTimeOffset.TryParse(
                    text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                    out var parsed))
                return parsed.ToUniversalTime();
        }

        throw new JsonException("Meta activity event_time was missing or invalid.");
    }

    private static string? ExtraDataText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Undefined or JsonValueKind.Null => null,
        JsonValueKind.String => value.GetString(),
        _ => value.GetRawText(),
    };

    private static string? ExtractStatus(string? extraDataJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(extraDataJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(extraDataJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase)) continue;
                return property.Value.ValueKind == JsonValueKind.String
                    ? NormalizeStatus(property.Value.GetString())
                    : NormalizeStatus(property.Value.ToString());
            }
        }
        catch (JsonException)
        {
            // Preserve the raw activity fact. An unfamiliar extra_data shape is
            // explicitly unjudgeable rather than guessed into a pause/resume.
        }

        return null;
    }

    private static string? NormalizeStatus(string? value)
    {
        var normalized = value?.Trim().Replace(' ', '_').ToUpperInvariant();
        return normalized switch
        {
            "ACTIVE" or "ENABLED" => "ACTIVE",
            "INACTIVE" or "PAUSED" or "CAMPAIGN_PAUSED" or "DISABLED" => "PAUSED",
            _ => null,
        };
    }

    private static string NormalizeActId(string adAccountId) =>
        adAccountId.StartsWith("act_", StringComparison.Ordinal) ? adAccountId : $"act_{adAccountId}";

    private sealed class ActivityEnvelopeJson
    {
        [JsonPropertyName("data")] public List<ActivityJson> Data { get; set; } = [];
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

    private sealed class ActivityJson
    {
        [JsonPropertyName("actor_id")] public string? ActorId { get; set; }
        [JsonPropertyName("actor_name")] public string? ActorName { get; set; }
        [JsonPropertyName("application_id")] public string? ApplicationId { get; set; }
        [JsonPropertyName("application_name")] public string? ApplicationName { get; set; }
        [JsonPropertyName("event_time")] public JsonElement EventTime { get; set; }
        [JsonPropertyName("event_type")] public string? EventType { get; set; }
        [JsonPropertyName("extra_data")] public JsonElement ExtraData { get; set; }
        [JsonPropertyName("object_id")] public string? ObjectId { get; set; }
        [JsonPropertyName("object_name")] public string? ObjectName { get; set; }
        [JsonPropertyName("object_type")] public string? ObjectType { get; set; }
        [JsonPropertyName("tool")] public string? Tool { get; set; }
        [JsonPropertyName("translated_event_type")] public string? TranslatedEventType { get; set; }
    }
}
