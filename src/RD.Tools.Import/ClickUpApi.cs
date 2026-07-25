using System.Text.Json;
using System.Text.RegularExpressions;

namespace RD.Tools.Import;

/// <summary>One ClickUp custom-field definition, with dropdown options resolvable by option-id or order-index.</summary>
public sealed record ClickUpField(string Id, string Name, string Type, Dictionary<string, string> OptionsByKey);

/// <summary>One ClickUp task with its custom-field values already resolved to human labels (dropdowns → option name).</summary>
public sealed record ClickUpTask(string Id, string Name, string Status, Dictionary<string, string> Fields)
{
    /// <summary>Trimmed value of a custom field by name, or null when blank/absent.</summary>
    public string? Field(string name) => Fields.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
}

/// <summary>
/// Thin read-only client for the ClickUp v2 API — fetch a list's custom-field
/// catalog and all its tasks (custom-field values resolved to labels), plus the
/// small parsers that pull ids out of the URL-shaped fields (GHL contact/location,
/// Meta campaign). Shared by the discovery probe and the trial importer.
/// </summary>
public static partial class ClickUpApi
{
    public static HttpClient CreateClient(string token)
    {
        var http = new HttpClient { BaseAddress = new Uri("https://api.clickup.com/api/v2/") };
        http.DefaultRequestHeaders.Add("Authorization", token); // personal pk_ tokens go raw, no "Bearer"
        return http;
    }

    public static async Task<Dictionary<string, ClickUpField>?> GetFieldsAsync(HttpClient http, string listId)
    {
        using var resp = await http.GetAsync($"list/{listId}/field");
        if (!resp.IsSuccessStatusCode)
        {
            Console.WriteLine($"ERROR: GET list/{listId}/field → {(int)resp.StatusCode} {resp.StatusCode}. Check the token and list id.");
            return null;
        }
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var map = new Dictionary<string, ClickUpField>(StringComparer.OrdinalIgnoreCase);
        if (doc.RootElement.TryGetProperty("fields", out var arr))
        {
            foreach (var f in arr.EnumerateArray())
            {
                var id = Str(f, "id") ?? "";
                var options = new Dictionary<string, string>();
                if (f.TryGetProperty("type_config", out var tc) && tc.TryGetProperty("options", out var opts))
                {
                    var idx = 0;
                    foreach (var o in opts.EnumerateArray())
                    {
                        var label = Str(o, "name") ?? Str(o, "label") ?? "";
                        if (Str(o, "id") is { } oid) options[oid] = label;
                        options[idx.ToString()] = label; // dropdowns commonly store the order-index
                        idx++;
                    }
                }
                map[id] = new ClickUpField(id, Str(f, "name") ?? "(unnamed)", Str(f, "type") ?? "?", options);
            }
        }
        return map;
    }

    /// <summary>All tasks on a list (paginated, include_closed, non-archived), custom-field values resolved to labels.</summary>
    public static async Task<List<ClickUpTask>> GetTasksAsync(HttpClient http, string listId, Dictionary<string, ClickUpField> fields)
    {
        var rows = new List<ClickUpTask>();
        for (var page = 0; page < 80; page++) // safety cap
        {
            using var resp = await http.GetAsync($"list/{listId}/task?page={page}&include_closed=true&subtasks=false");
            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"WARNING: GET tasks page {page} → {(int)resp.StatusCode}; stopping pagination.");
                break;
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("tasks", out var arr)) break;

            var pageCount = 0;
            foreach (var t in arr.EnumerateArray())
            {
                pageCount++;
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (t.TryGetProperty("custom_fields", out var cfs))
                {
                    foreach (var cf in cfs.EnumerateArray())
                    {
                        var name = Str(cf, "name") ?? "";
                        if (name.Length == 0 || !cf.TryGetProperty("value", out var v)) continue;
                        var raw = RawValue(v);
                        var fieldId = Str(cf, "id") ?? "";
                        values[name] = fields.TryGetValue(fieldId, out var def) && def.OptionsByKey.TryGetValue(raw, out var label)
                            ? label
                            : raw;
                    }
                }
                var status = t.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Object
                    ? Str(st, "status") ?? "?" : "?";
                rows.Add(new ClickUpTask(Str(t, "id") ?? "", Str(t, "name") ?? "(unnamed)", status, values));
            }
            if (pageCount < 100) break; // last page
        }
        return rows;
    }

    // ── URL-shaped field parsers ────────────────────────────────────────────────

    /// <summary>Pulls the contact id from a GHL contact URL (…/contacts/detail/{id}).</summary>
    public static string? GhlContactId(string? url)
        => Match(url, GhlContactRegex());

    /// <summary>Pulls the location id from a GHL contact URL (…/location/{id}/contacts…).</summary>
    public static string? GhlLocationId(string? url)
        => Match(url, GhlLocationRegex());

    /// <summary>Pulls a Meta campaign id from an Ads Manager URL (selected_campaign_ids={id}).</summary>
    public static string? MetaCampaignId(string? url)
        => Match(url, MetaCampaignRegex());

    /// <summary>Pulls a Meta ad-account id from an Ads Manager URL (act={digits}).</summary>
    public static string? MetaAdAccountFromUrl(string? url)
        => Match(url, MetaActRegex());

    private static string? Match(string? input, Regex rx)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var m = rx.Match(input);
        return m.Success ? m.Groups[1].Value : null;
    }

    [GeneratedRegex(@"/contacts/detail/([A-Za-z0-9]+)")] private static partial Regex GhlContactRegex();
    [GeneratedRegex(@"/location/([A-Za-z0-9]+)")] private static partial Regex GhlLocationRegex();
    [GeneratedRegex(@"selected_campaign_ids=(\d+)")] private static partial Regex MetaCampaignRegex();
    [GeneratedRegex(@"[?&]act=(\d+)")] private static partial Regex MetaActRegex();

    // ── JSON helpers ────────────────────────────────────────────────────────────

    internal static string RawValue(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? "",
        JsonValueKind.Number => v.ToString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Array => string.Join("|", v.EnumerateArray().Select(RawValue)),
        JsonValueKind.Object => Str(v, "value") ?? v.GetRawText(),
        _ => "",
    };

    internal static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
