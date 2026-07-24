using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace RD.Infrastructure.Gateways;

public interface IGhlGateway
{
    /// <summary>Most-recent conversations for a location, sorted last_message_date desc.</summary>
    Task<IReadOnlyList<GhlConversationDto>> SearchConversationsAsync(
        string locationId, int limit, CancellationToken ct);

    /// <summary>Messages for one conversation. Sync cares about outbound only (delivery evidence).</summary>
    Task<IReadOnlyList<GhlMessageDto>> GetMessagesAsync(
        string locationId, string conversationId, CancellationToken ct);
}

public sealed record GhlConversationDto(string Id, string? ContactId);

public sealed record GhlMessageDto(
    string Id,
    string? ContactId,
    string Direction,
    string MessageType,
    string? Body,
    DateTimeOffset DateAdded);

/// <summary>
/// Raw-HttpClient GoHighLevel reads. Auth is a PER-LOCATION Private Integration
/// Token (exactly 2 locations in production), resolved per request from
/// configuration — tokens never leave the Authorization header and error
/// messages carry only the location id.
/// </summary>
public sealed class GhlGateway : IGhlGateway
{
    /// <summary>Pinned GHL API version header (proven in the M0 auth spike).</summary>
    public const string ApiVersion = "2021-07-28";

    private const int MessagePageSize = 100;

    private readonly HttpClient _http;
    private readonly GhlOptions _options;
    private readonly RetryHelper _retry;

    public GhlGateway(HttpClient http, IOptions<GhlOptions> options, RetryHelper retry)
    {
        _http = http;
        _options = options.Value;
        _retry = retry;
        _http.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        _http.DefaultRequestHeaders.Add("Version", ApiVersion);
    }

    public async Task<IReadOnlyList<GhlConversationDto>> SearchConversationsAsync(
        string locationId, int limit, CancellationToken ct)
    {
        var token = TokenFor(locationId);
        var url = $"conversations/search?locationId={Uri.EscapeDataString(locationId)}" +
                  $"&limit={limit}&sortBy=last_message_date&sort=desc";

        using var response = await _retry.SendAsync(_http, () => BuildRequest(url, token), ct);
        var envelope = await GatewayHttp.ReadAsAsync<SearchJson>(response, ct);
        return envelope.Conversations
            .Where(c => !string.IsNullOrEmpty(c.Id))
            .Select(c => new GhlConversationDto(c.Id!, c.ContactId))
            .ToList();
    }

    public async Task<IReadOnlyList<GhlMessageDto>> GetMessagesAsync(
        string locationId, string conversationId, CancellationToken ct)
    {
        var token = TokenFor(locationId);
        var url = $"conversations/{Uri.EscapeDataString(conversationId)}/messages?limit={MessagePageSize}";

        using var response = await _retry.SendAsync(_http, () => BuildRequest(url, token), ct);
        using var doc = await GatewayHttp.ReadDocumentAsync(response, ct);

        // Shape-tolerant: the documented envelope is { messages: { messages: [...] } },
        // but some responses flatten to { messages: [...] }.
        var results = new List<GhlMessageDto>();
        if (!doc.RootElement.TryGetProperty("messages", out var messagesNode)) return results;

        var array = messagesNode.ValueKind switch
        {
            JsonValueKind.Array => messagesNode,
            JsonValueKind.Object when messagesNode.TryGetProperty("messages", out var inner)
                                      && inner.ValueKind == JsonValueKind.Array => inner,
            _ => default,
        };
        if (array.ValueKind != JsonValueKind.Array) return results;

        foreach (var element in array.EnumerateArray())
        {
            if (ParseMessage(element) is { } message) results.Add(message);
        }
        return results;
    }

    private static GhlMessageDto? ParseMessage(JsonElement el)
    {
        var id = GetString(el, "id");
        if (string.IsNullOrEmpty(id)) return null;

        return new GhlMessageDto(
            id,
            GetString(el, "contactId"),
            GetString(el, "direction") ?? "",
            GetString(el, "messageType") ?? "Unknown",
            GetString(el, "body"),
            ParseDate(el) ?? DateTimeOffset.MinValue);
    }

    private static DateTimeOffset? ParseDate(JsonElement el)
    {
        if (!el.TryGetProperty("dateAdded", out var value)) return null;
        return value.ValueKind switch
        {
            // ISO-8601 string is the documented shape; epoch millis appear in some payloads.
            JsonValueKind.String when DateTimeOffset.TryParse(
                value.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var parsed) => parsed,
            JsonValueKind.Number when value.TryGetInt64(out var epochMs) =>
                DateTimeOffset.FromUnixTimeMilliseconds(epochMs),
            _ => null,
        };
    }

    private static string? GetString(JsonElement el, string property) =>
        el.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private HttpRequestMessage BuildRequest(string relativeUrl, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private string TokenFor(string locationId)
    {
        var location = _options.Locations.FirstOrDefault(l => l.LocationId == locationId);
        if (location is null || string.IsNullOrEmpty(location.Token))
            throw new InvalidOperationException($"No GHL token configured for location '{locationId}' (Ghl:Locations).");
        return location.Token;
    }

    private sealed class SearchJson
    {
        [JsonPropertyName("conversations")] public List<ConversationJson> Conversations { get; set; } = [];
    }

    private sealed class ConversationJson
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("contactId")] public string? ContactId { get; set; }
    }
}
