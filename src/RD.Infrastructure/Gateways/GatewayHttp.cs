using System.Text.Json;

namespace RD.Infrastructure.Gateways;

/// <summary>Shared response plumbing for the raw-HttpClient gateways.</summary>
internal static class GatewayHttp
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Throws HttpRequestException (with status + a short body snippet) on
    /// non-success, otherwise deserializes the body. The exception message
    /// carries the request path only — never query strings or headers, so
    /// credentials can never leak into logs.
    /// </summary>
    public static async Task<T> ReadAsAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        await EnsureSuccessAsync(response, ct);
        var stream = await response.Content.ReadAsStreamAsync(ct);
        var result = await JsonSerializer.DeserializeAsync<T>(stream, Json, ct);
        return result ?? throw new HttpRequestException($"Empty JSON body from {SafePath(response)}");
    }

    /// <summary>Same as ReadAsAsync but hands back the raw document (caller disposes) for shape-tolerant parsing.</summary>
    public static async Task<JsonDocument> ReadDocumentAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await EnsureSuccessAsync(response, ct);
        var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        var snippet = "";
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            snippet = body.Length > 300 ? body[..300] : body;
        }
        catch
        {
            // Best-effort diagnostics only.
        }

        throw new HttpRequestException(
            $"Vendor API returned {(int)response.StatusCode} for {SafePath(response)}: {snippet}",
            inner: null, statusCode: response.StatusCode);
    }

    private static string? SafePath(HttpResponseMessage response) =>
        response.RequestMessage?.RequestUri?.GetLeftPart(UriPartial.Path);
}
