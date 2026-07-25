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

    /// <summary>
    /// Searches contacts in one location by a free-text query (GHL matches across
    /// name / email / phone), using the supplied token directly so callers can
    /// sweep several locations and match on whichever field they have. Read-only.
    /// </summary>
    Task<IReadOnlyList<GhlContactDto>> SearchContactsAsync(string? locationId, string token, string query, CancellationToken ct);

    /// <summary>Write a contact custom field (e.g. the hosted invoice URL a dunning workflow renders). TestMode redirects to the test contact.</summary>
    Task<GhlWriteResult> SetContactFieldAsync(
        string locationId, string contactId, string fieldKey, string value, CancellationToken ct);

    /// <summary>Add the contact to a workflow (fires the dunning message). TestMode redirects to the test contact.</summary>
    Task<GhlWriteResult> TriggerWorkflowAsync(
        string locationId, string contactId, string workflowId, CancellationToken ct);

    /// <summary>
    /// Creates a contact in one location (email + name), returning its id. GHL
    /// dedupes on email, so an existing contact is returned rather than duplicated.
    /// A WRITE — admin-initiated only (not TestMode-gated; it creates a CRM record,
    /// it does not message anyone).
    /// </summary>
    Task<string> CreateContactAsync(string locationId, string token, string? email, string name, CancellationToken ct);

    /// <summary>
    /// A contact's current tags. Read-only (no TestMode gate) — this is the read-before-write check for
    /// the `close` tag write: GHL doesn't guarantee re-adding an existing tag is a no-op, so a write must
    /// skip when the tag is already present or risk double-firing the tag's workflows.
    /// </summary>
    Task<IReadOnlyList<string>> GetContactTagsAsync(string locationId, string contactId, CancellationToken ct);

    /// <summary>
    /// Add a tag to a contact (POST /contacts/{id}/tags). A WRITE that detonates whatever GHL workflows
    /// trigger on that tag — TestMode redirects it to the test contact so it can never fire a real client's
    /// chain. Callers MUST read-before-write (see <see cref="GetContactTagsAsync"/>).
    /// </summary>
    Task<GhlWriteResult> AddContactTagAsync(string locationId, string contactId, string tag, CancellationToken ct);
}

/// <summary>Records where the write ACTUALLY landed — under TestMode that is the test contact, not the intended recipient. The audit must show the redirect.</summary>
public sealed record GhlWriteResult(bool Redirected, string EffectiveLocationId, string EffectiveContactId);

public sealed record GhlConversationDto(string Id, string? ContactId);

public sealed record GhlContactDto(string Id, string? Email, string? Phone, string? Name);

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
    private readonly SafetyOptions _safety;
    private readonly RetryHelper _retry;

    public GhlGateway(HttpClient http, IOptions<GhlOptions> options, IOptions<SafetyOptions> safety, RetryHelper retry)
    {
        _http = http;
        _options = options.Value;
        _safety = safety.Value;
        _retry = retry;
        _http.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        _http.DefaultRequestHeaders.Add("Version", ApiVersion);
    }

    public async Task<GhlWriteResult> SetContactFieldAsync(
        string locationId, string contactId, string fieldKey, string value, CancellationToken ct)
    {
        var (effLocation, effContact, redirected) = ResolveTarget(locationId, contactId);
        var token = TokenFor(effLocation);
        var payload = JsonSerializer.Serialize(new
        {
            customFields = new[] { new { key = fieldKey, field_value = value } },
        });

        using var request = BuildJsonRequest(HttpMethod.Put, $"contacts/{Uri.EscapeDataString(effContact)}", token, payload);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureWriteSucceededAsync(response, ct);
        return new GhlWriteResult(redirected, effLocation, effContact);
    }

    public async Task<GhlWriteResult> TriggerWorkflowAsync(
        string locationId, string contactId, string workflowId, CancellationToken ct)
    {
        var (effLocation, effContact, redirected) = ResolveTarget(locationId, contactId);
        var token = TokenFor(effLocation);

        using var request = BuildJsonRequest(
            HttpMethod.Post,
            $"contacts/{Uri.EscapeDataString(effContact)}/workflow/{Uri.EscapeDataString(workflowId)}",
            token, "{}");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureWriteSucceededAsync(response, ct);
        return new GhlWriteResult(redirected, effLocation, effContact);
    }

    public async Task<IReadOnlyList<string>> GetContactTagsAsync(string locationId, string contactId, CancellationToken ct)
    {
        var token = TokenFor(locationId);
        using var response = await _retry.SendAsync(
            _http, () => BuildRequest($"contacts/{Uri.EscapeDataString(contactId)}", token), ct);
        using var doc = await GatewayHttp.ReadDocumentAsync(response, ct);

        // { contact: { tags: [...] } } is the documented shape; tolerate a flattened { tags: [...] }.
        var root = doc.RootElement;
        var contact = root.TryGetProperty("contact", out var c) && c.ValueKind == JsonValueKind.Object ? c : root;
        if (!contact.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array) return [];
        return tags.EnumerateArray()
            .Where(t => t.ValueKind == JsonValueKind.String)
            .Select(t => t.GetString()!)
            .ToList();
    }

    public async Task<GhlWriteResult> AddContactTagAsync(string locationId, string contactId, string tag, CancellationToken ct)
    {
        var (effLocation, effContact, redirected) = ResolveTarget(locationId, contactId);
        var token = TokenFor(effLocation);
        var payload = JsonSerializer.Serialize(new { tags = new[] { tag } });

        using var request = BuildJsonRequest(
            HttpMethod.Post, $"contacts/{Uri.EscapeDataString(effContact)}/tags", token, payload);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureWriteSucceededAsync(response, ct);
        return new GhlWriteResult(redirected, effLocation, effContact);
    }

    /// <summary>
    /// The TestMode gate lives HERE, inside the gateway — not at call sites —
    /// so no code path can accidentally text a real client. When on, every
    /// send goes to the configured test contact regardless of the intended
    /// recipient, and the result records that it was redirected.
    /// </summary>
    private (string location, string contact, bool redirected) ResolveTarget(string locationId, string contactId)
    {
        if (!_safety.GhlTestMode) return (locationId, contactId, false);
        if (string.IsNullOrEmpty(_safety.TestContactLocationId) || string.IsNullOrEmpty(_safety.TestContactId))
            throw new InvalidOperationException(
                "Safety:GhlTestMode is on but no test contact is configured (Safety:TestContactLocationId / Safety:TestContactId). " +
                "Refusing to send rather than risk texting a real client.");
        return (_safety.TestContactLocationId, _safety.TestContactId, true);
    }

    private static async Task EnsureWriteSucceededAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = "";
        try { var s = await response.Content.ReadAsStringAsync(ct); body = s.Length > 300 ? s[..300] : s; } catch { }
        throw new HttpRequestException(
            $"GHL write returned {(int)response.StatusCode} for {response.RequestMessage?.RequestUri?.GetLeftPart(UriPartial.Path)}: {body}",
            inner: null, statusCode: response.StatusCode);
    }

    private HttpRequestMessage BuildJsonRequest(HttpMethod method, string relativeUrl, string token, string json)
    {
        var request = new HttpRequestMessage(method, relativeUrl)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
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

    public async Task<string> CreateContactAsync(string locationId, string token, string? email, string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(locationId)) throw new ArgumentException("A GHL location id is required to create a contact.", nameof(locationId));

        var payload = JsonSerializer.Serialize(new { locationId, email, name });
        using var request = BuildJsonRequest(HttpMethod.Post, "contacts/", token, payload);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        // Read the body regardless of status: a "duplicated contact" (409/400) still
        // carries the existing contact's id in meta.contactId, which is what we want.
        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        var root = doc.RootElement;

        if (root.TryGetProperty("contact", out var contact)
            && contact.TryGetProperty("id", out var idEl) && idEl.GetString() is { Length: > 0 } id)
            return id;

        if (root.TryGetProperty("meta", out var meta)
            && meta.TryGetProperty("contactId", out var dupEl) && dupEl.GetString() is { Length: > 0 } dupId)
            return dupId; // GHL says it already exists (deduped on email) — link that one

        var snippet = body.Length > 200 ? body[..200] : body;
        throw new HttpRequestException($"GHL create-contact returned {(int)response.StatusCode} with no contact id: {snippet}");
    }

    public async Task<IReadOnlyList<GhlContactDto>> SearchContactsAsync(string? locationId, string token, string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(query)) return [];

        var url = $"contacts/?query={Uri.EscapeDataString(query)}&limit=20";
        if (!string.IsNullOrEmpty(locationId)) url += $"&locationId={Uri.EscapeDataString(locationId)}";

        using var response = await _retry.SendAsync(_http, () => BuildRequest(url, token), ct);
        if (!response.IsSuccessStatusCode) return []; // wrong location / no access — caller tries the next key

        using var doc = await GatewayHttp.ReadDocumentAsync(response, ct);
        if (!doc.RootElement.TryGetProperty("contacts", out var contacts) || contacts.ValueKind != JsonValueKind.Array)
            return [];

        var results = new List<GhlContactDto>();
        foreach (var c in contacts.EnumerateArray())
        {
            var id = GetString(c, "id");
            if (string.IsNullOrEmpty(id)) continue;
            var name = GetString(c, "contactName")
                       ?? $"{GetString(c, "firstName")} {GetString(c, "lastName")}".Trim();
            results.Add(new GhlContactDto(id, GetString(c, "email"), GetString(c, "phone"),
                string.IsNullOrWhiteSpace(name) ? null : name));
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
