using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RD.Domain;
using RD.Infrastructure.Enforcement;

namespace RD.Infrastructure.Slack;

public static class SlackInteractivityEndpoints
{
    private const string TimestampHeader = "X-Slack-Request-Timestamp";
    private const string SignatureHeader = "X-Slack-Signature";

    /// <summary>
    /// Maps POST /slack/interactivity — the callback Slack hits when an Operator
    /// clicks Approve/Dismiss. The body is read RAW (the signature covers the exact
    /// bytes Slack sent) and verified BEFORE anything else; a bad signature or a
    /// stale timestamp returns 400 with no detail (leak nothing to a prober).
    ///
    /// A Slack signature authenticates only the workspace (OV 15), so the clicking
    /// Slack user is mapped to an internal Operator via <see cref="SlackAuthorizer"/>;
    /// an unlinked/unauthorized user gets an ephemeral "not authorized" reply and NO
    /// action is taken. An authorized click runs the same <c>ApprovalService</c> CAS
    /// as the cockpit (first channel wins) and replaces the buttons with the outcome.
    /// </summary>
    public static IEndpointRouteBuilder MapSlackInteractivity(this IEndpointRouteBuilder app)
    {
        app.MapPost("/slack/interactivity", async (
                HttpContext context,
                SlackSignatureVerifier verifier,
                SlackAuthorizer authorizer,
                ApprovalService approvals,
                CancellationToken ct) =>
            {
                string rawBody;
                using (var reader = new StreamReader(context.Request.Body, leaveOpen: true))
                    rawBody = await reader.ReadToEndAsync(ct);

                var timestamp = context.Request.Headers[TimestampHeader].ToString();
                var signature = context.Request.Headers[SignatureHeader].ToString();
                if (verifier.Verify(rawBody, timestamp, signature) != SlackSignatureVerification.Valid)
                    return Results.StatusCode(StatusCodes.Status400BadRequest);

                // application/x-www-form-urlencoded with a single `payload` field = URL-encoded JSON.
                if (!TryGetFormField(rawBody, "payload", out var payloadJson))
                    return Results.StatusCode(StatusCodes.Status400BadRequest);

                var interaction = SlackInteractionParser.Parse(payloadJson);
                if (interaction is null)
                    return Results.StatusCode(StatusCodes.Status400BadRequest);

                // OV 15: the signature proves the workspace, not the person. Map to an Operator.
                var auth = await authorizer.AuthorizeAsync(interaction.SlackUserId, ct);
                if (!auth.Authorized)
                    return Results.Json(SlackResponses.Unauthorized(), statusCode: StatusCodes.Status200OK);

                if (!Guid.TryParse(interaction.Value, out var actionId))
                    return Results.StatusCode(StatusCodes.Status400BadRequest);

                var name = auth.UserName!;
                var outcome = interaction.ActionId switch
                {
                    "rd_approve" => await approvals.ApproveAsync(actionId, ApprovalChannel.Slack, name, ct),
                    "rd_dismiss" => await approvals.DismissAsync(actionId, ApprovalChannel.Slack, name, "Dismissed from Slack", ct),
                    _ => ApprovalOutcome.NotFound,
                };

                return Results.Json(SlackResponses.ForOutcome(outcome, name), statusCode: StatusCodes.Status200OK);
            })
            // A signed Slack callback carries no antiforgery token — and does not need one.
            .DisableAntiforgery();

        return app;
    }

    /// <summary>Pulls one field out of an x-www-form-urlencoded body ('+' decodes to space per the spec).</summary>
    private static bool TryGetFormField(string body, string field, out string value)
    {
        foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            var key = WebUtility.UrlDecode(pair[..eq]);
            if (!string.Equals(key, field, StringComparison.Ordinal)) continue;
            value = WebUtility.UrlDecode(pair[(eq + 1)..]);
            return !string.IsNullOrEmpty(value);
        }
        value = "";
        return false;
    }
}

/// <summary>The three fields we need from a block_actions payload.</summary>
public sealed record SlackInteraction(string SlackUserId, string ActionId, string Value);

internal static class SlackInteractionParser
{
    /// <summary>Extracts user.id, actions[0].action_id and actions[0].value; returns null on any malformed shape.</summary>
    public static SlackInteraction? Parse(string payloadJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("user", out var user) || user.ValueKind != JsonValueKind.Object)
                return null;
            var userId = Str(user, "id");
            if (string.IsNullOrEmpty(userId))
                return null;

            if (!root.TryGetProperty("actions", out var actions)
                || actions.ValueKind != JsonValueKind.Array
                || actions.GetArrayLength() == 0)
                return null;

            var first = actions[0];
            var actionId = Str(first, "action_id");
            var value = Str(first, "value");
            if (string.IsNullOrEmpty(actionId) || string.IsNullOrEmpty(value))
                return null;

            return new SlackInteraction(userId, actionId, value);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
}

/// <summary>Block Kit response bodies Slack renders in place of the message that carried the buttons.</summary>
internal static class SlackResponses
{
    /// <summary>Ephemeral: only the clicker sees it, the original message and its buttons stay put.</summary>
    public static object Unauthorized() => new
    {
        response_type = "ephemeral",
        replace_original = false,
        text = "You're not authorized — your Slack account isn't linked to an Operator.",
    };

    /// <summary>In-channel: replaces the original message (removing the buttons) with the resolved outcome.</summary>
    public static object ForOutcome(ApprovalOutcome outcome, string userName)
    {
        var text = outcome switch
        {
            ApprovalOutcome.Approved => $"✅ Approved by {userName}",
            ApprovalOutcome.Dismissed => $"❌ Dismissed by {userName}",
            _ => "Already resolved", // AlreadyResolved or NotFound — the other channel won, or it's gone.
        };

        return new
        {
            response_type = "in_channel",
            replace_original = true,
            text,
            blocks = new object[]
            {
                new { type = "section", text = new { type = "mrkdwn", text } },
            },
        };
    }
}
