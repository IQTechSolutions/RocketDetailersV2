using System.Net;

namespace RD.Infrastructure.Gateways;

/// <summary>
/// Minimal retry for vendor HTTP reads: 3 attempts total, exponential backoff
/// on transport errors / 429 / 5xx, honoring Retry-After when present.
/// Deliberately not Polly — one small policy we fully own, no supply-chain
/// surprises. (Outbox writes have their own single retry authority; this
/// helper is for read-side gateway calls only.)
/// </summary>
public sealed class RetryHelper
{
    public int MaxAttempts { get; init; } = 3;
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Sends the request produced by <paramref name="requestFactory"/> (a fresh
    /// HttpRequestMessage per attempt — messages are single-use). Returns the
    /// final response, which may still be a failure after retries are exhausted;
    /// callers surface that via their ensure-success path.
    /// </summary>
    public async Task<HttpResponseMessage> SendAsync(
        HttpClient http, Func<HttpRequestMessage> requestFactory, CancellationToken ct)
    {
        HttpResponseMessage? response = null;
        for (var attempt = 1; ; attempt++)
        {
            HttpRequestException? transportError = null;
            try
            {
                response = await http.SendAsync(requestFactory(), HttpCompletionOption.ResponseHeadersRead, ct);
                if (!IsTransient(response.StatusCode))
                    return response;
            }
            catch (HttpRequestException ex)
            {
                transportError = ex;
            }

            if (attempt >= MaxAttempts)
            {
                if (transportError is not null) throw transportError;
                return response!;
            }

            var delay = RetryAfter(response) ?? Backoff(attempt);
            if (delay > MaxDelay) delay = MaxDelay;

            response?.Dispose();
            response = null;
            await Task.Delay(delay, ct);
        }
    }

    private TimeSpan Backoff(int attempt) => TimeSpan.FromTicks(BaseDelay.Ticks << (attempt - 1));

    private static bool IsTransient(HttpStatusCode code) =>
        code == HttpStatusCode.TooManyRequests || (int)code >= 500;

    private static TimeSpan? RetryAfter(HttpResponseMessage? response)
    {
        var retryAfter = response?.Headers.RetryAfter;
        if (retryAfter is null) return null;
        if (retryAfter.Delta is { } delta) return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
        if (retryAfter.Date is { } when)
        {
            var wait = when - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
        return null;
    }
}
