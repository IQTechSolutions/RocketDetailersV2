using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace RD.Infrastructure.Gateways;

public interface IStripeGateway
{
    /// <summary>Full paginated sweep of all subscriptions (status=all).</summary>
    Task<IReadOnlyList<StripeSubscriptionDto>> ListSubscriptionsAsync(CancellationToken ct);

    /// <summary>
    /// Paginated invoice listing. <paramref name="status"/> filters by Stripe
    /// invoice status (e.g. "open"); <paramref name="createdOnOrAfter"/> maps
    /// to created[gte]. The sync job calls this twice: all open invoices
    /// (regardless of age) plus a recent window (catches paid + uncollectible).
    /// </summary>
    Task<IReadOnlyList<StripeInvoiceDto>> ListInvoicesAsync(
        string? status, DateTimeOffset? createdOnOrAfter, CancellationToken ct);
}

/// <summary>Amounts are converted from Stripe minor units to major-unit decimals.</summary>
public sealed record StripeSubscriptionDto(
    string Id,
    string CustomerId,
    string Status,
    string CurrencyCode,
    string? PriceInterval,
    decimal? Amount,
    DateTimeOffset? CanceledAt);

public sealed record StripeInvoiceDto(
    string Id,
    string CustomerId,
    string? SubscriptionId,
    string Status,
    decimal AmountDue,
    decimal AmountPaid,
    string CurrencyCode,
    string? HostedInvoiceUrl,
    DateTimeOffset Created,
    DateTimeOffset? DueDate,
    DateTimeOffset? PaidAt);

/// <summary>
/// Raw-HttpClient Stripe reads (no vendor SDK — WireMock-testable, no
/// supply-chain surprises). Auth via Bearer restricted key; the API version is
/// pinned so vendor-side default-version bumps can never change payload shapes
/// under us.
/// </summary>
public sealed class StripeGateway : IStripeGateway
{
    private const int PageSize = 100;
    private const int MaxPages = 200; // safety valve against a vendor-side pagination loop

    private readonly HttpClient _http;
    private readonly RetryHelper _retry;

    public StripeGateway(HttpClient http, IOptions<StripeOptions> options, RetryHelper retry)
    {
        _http = http;
        _retry = retry;
        var o = options.Value;
        _http.BaseAddress = new Uri(o.BaseUrl.TrimEnd('/') + "/");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", o.ApiKey);
        // Pin via "Stripe:ApiVersion" once the account's version is confirmed
        // in M0 — an unset pin uses the ACCOUNT default, which is stable per
        // account; a wrong guess hard-fails every request (seen live: 400
        // "Invalid Stripe API version"). Bump deliberately, with tests.
        if (!string.IsNullOrEmpty(o.ApiVersion))
            _http.DefaultRequestHeaders.Add("Stripe-Version", o.ApiVersion);
    }

    public async Task<IReadOnlyList<StripeSubscriptionDto>> ListSubscriptionsAsync(CancellationToken ct)
    {
        var results = new List<StripeSubscriptionDto>();
        string? startingAfter = null;
        for (var page = 0; ; page++)
        {
            if (page >= MaxPages) throw new InvalidOperationException("Stripe subscription pagination exceeded the page cap.");

            var url = $"v1/subscriptions?limit={PageSize}&status=all";
            if (startingAfter is not null) url += $"&starting_after={Uri.EscapeDataString(startingAfter)}";

            var envelope = await GetAsync<StripeListJson<SubscriptionJson>>(url, ct);
            results.AddRange(envelope.Data.Where(s => s.Id.Length > 0).Select(ToDto));

            if (!envelope.HasMore || envelope.Data.Count == 0) return results;
            startingAfter = envelope.Data[^1].Id;
        }
    }

    public async Task<IReadOnlyList<StripeInvoiceDto>> ListInvoicesAsync(
        string? status, DateTimeOffset? createdOnOrAfter, CancellationToken ct)
    {
        var results = new List<StripeInvoiceDto>();
        string? startingAfter = null;
        for (var page = 0; ; page++)
        {
            if (page >= MaxPages) throw new InvalidOperationException("Stripe invoice pagination exceeded the page cap.");

            var url = $"v1/invoices?limit={PageSize}";
            if (status is not null) url += $"&status={Uri.EscapeDataString(status)}";
            if (createdOnOrAfter is { } created) url += $"&created[gte]={created.ToUnixTimeSeconds()}";
            if (startingAfter is not null) url += $"&starting_after={Uri.EscapeDataString(startingAfter)}";

            var envelope = await GetAsync<StripeListJson<InvoiceJson>>(url, ct);
            results.AddRange(envelope.Data
                .Where(i => i.Id.Length > 0 && !string.IsNullOrEmpty(i.Customer))
                .Select(ToDto));

            if (!envelope.HasMore || envelope.Data.Count == 0) return results;
            startingAfter = envelope.Data[^1].Id;
        }
    }

    private async Task<T> GetAsync<T>(string relativeUrl, CancellationToken ct)
    {
        using var response = await _retry.SendAsync(
            _http, () => new HttpRequestMessage(HttpMethod.Get, relativeUrl), ct);
        return await GatewayHttp.ReadAsAsync<T>(response, ct);
    }

    private static StripeSubscriptionDto ToDto(SubscriptionJson s)
    {
        var price = s.Items?.Data.FirstOrDefault()?.Price;
        return new StripeSubscriptionDto(
            s.Id,
            s.Customer ?? "",
            s.Status ?? "",
            (price?.Currency ?? s.Currency ?? "usd").ToUpperInvariant(),
            price?.Recurring?.Interval,
            MinorToDecimal(price?.UnitAmount),
            FromUnix(s.CanceledAt));
    }

    private static StripeInvoiceDto ToDto(InvoiceJson i) => new(
        i.Id,
        i.Customer!,
        // Basil API versions moved invoice.subscription under parent.subscription_details;
        // parse both shapes so a pin bump can't silently drop the join key.
        i.Subscription ?? i.Parent?.SubscriptionDetails?.Subscription,
        i.Status ?? "",
        MinorToDecimal(i.AmountDue) ?? 0m,
        MinorToDecimal(i.AmountPaid) ?? 0m,
        (i.Currency ?? "usd").ToUpperInvariant(),
        i.HostedInvoiceUrl,
        DateTimeOffset.FromUnixTimeSeconds(i.Created),
        FromUnix(i.DueDate),
        FromUnix(i.StatusTransitions?.PaidAt));

    /// <summary>
    /// Stripe amounts are minor units (cents). The client book is USD/CAD (both
    /// 2-decimal); zero-decimal currencies (JPY-class) would need a divisor
    /// table and are out of scope for v1 — non-USD/CAD already routes to
    /// needs-investigation at the policy layer.
    /// </summary>
    private static decimal? MinorToDecimal(long? minor) => minor is { } m ? m / 100m : null;

    private static DateTimeOffset? FromUnix(long? seconds) =>
        seconds is { } s ? DateTimeOffset.FromUnixTimeSeconds(s) : null;

    private sealed class StripeListJson<T>
    {
        [JsonPropertyName("data")] public List<T> Data { get; set; } = [];
        [JsonPropertyName("has_more")] public bool HasMore { get; set; }
    }

    private sealed class SubscriptionJson
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("customer")] public string? Customer { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("canceled_at")] public long? CanceledAt { get; set; }
        [JsonPropertyName("items")] public ItemsJson? Items { get; set; }
    }

    private sealed class ItemsJson
    {
        [JsonPropertyName("data")] public List<ItemJson> Data { get; set; } = [];
    }

    private sealed class ItemJson
    {
        [JsonPropertyName("price")] public PriceJson? Price { get; set; }
    }

    private sealed class PriceJson
    {
        [JsonPropertyName("unit_amount")] public long? UnitAmount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("recurring")] public RecurringJson? Recurring { get; set; }
    }

    private sealed class RecurringJson
    {
        [JsonPropertyName("interval")] public string? Interval { get; set; }
    }

    private sealed class InvoiceJson
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("customer")] public string? Customer { get; set; }
        [JsonPropertyName("subscription")] public string? Subscription { get; set; }
        [JsonPropertyName("parent")] public ParentJson? Parent { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("amount_due")] public long AmountDue { get; set; }
        [JsonPropertyName("amount_paid")] public long AmountPaid { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("hosted_invoice_url")] public string? HostedInvoiceUrl { get; set; }
        [JsonPropertyName("created")] public long Created { get; set; }
        [JsonPropertyName("due_date")] public long? DueDate { get; set; }
        [JsonPropertyName("status_transitions")] public StatusTransitionsJson? StatusTransitions { get; set; }
    }

    private sealed class ParentJson
    {
        [JsonPropertyName("subscription_details")] public SubscriptionDetailsJson? SubscriptionDetails { get; set; }
    }

    private sealed class SubscriptionDetailsJson
    {
        [JsonPropertyName("subscription")] public string? Subscription { get; set; }
    }

    private sealed class StatusTransitionsJson
    {
        [JsonPropertyName("paid_at")] public long? PaidAt { get; set; }
    }
}
