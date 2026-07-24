using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace RD.Infrastructure.Webhooks;

public static class WebhookEndpoints
{
    /// <summary>
    /// Maps POST /webhooks/stripe. The body is read as the RAW string — never
    /// model-bound — because the signature covers the exact bytes Stripe sent.
    /// Verification runs BEFORE any parsing or side effect; an invalid signature
    /// or unparseable envelope returns 400 with no body detail (leak nothing to a
    /// prober). A verified, well-formed event always returns 200: the ingestor
    /// records it (Processed, AlreadyProcessed, or Poisoned) so Stripe stops
    /// redelivering — a poisoned event is a work-queue item, not an HTTP failure.
    /// </summary>
    public static IEndpointRouteBuilder MapStripeWebhook(this IEndpointRouteBuilder app)
    {
        app.MapPost("/webhooks/stripe", async (
                HttpContext context,
                StripeSignatureVerifier verifier,
                StripeWebhookIngestor ingestor,
                CancellationToken ct) =>
            {
                string payload;
                using (var reader = new StreamReader(context.Request.Body, leaveOpen: true))
                    payload = await reader.ReadToEndAsync(ct);

                var signatureHeader = context.Request.Headers["Stripe-Signature"].ToString();
                if (verifier.Verify(payload, signatureHeader) != StripeSignatureVerification.Valid)
                    return Results.StatusCode(StatusCodes.Status400BadRequest);

                string? eventId;
                string? eventType;
                try
                {
                    using var doc = JsonDocument.Parse(payload);
                    var root = doc.RootElement;
                    eventId = root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                        ? id.GetString() : null;
                    eventType = root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                        ? type.GetString() : null;
                }
                catch (JsonException)
                {
                    return Results.StatusCode(StatusCodes.Status400BadRequest);
                }

                if (string.IsNullOrEmpty(eventId) || string.IsNullOrEmpty(eventType))
                    return Results.StatusCode(StatusCodes.Status400BadRequest);

                await ingestor.IngestAsync(eventId, eventType, payload, ct);
                return Results.Ok();
            })
            // A signed vendor callback carries no antiforgery token — and does not need one.
            .DisableAntiforgery();

        return app;
    }
}
