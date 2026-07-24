using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RD.Domain.Entities;

namespace RD.Infrastructure.Slack;

/// <summary>
/// Posts a Block Kit message to the configured Slack incoming webhook announcing
/// one AwaitingApproval action, with interactive Approve/Dismiss buttons. The
/// button <c>value</c> is the OutboxAction id; the interactivity callback maps the
/// clicking Slack user to an internal Operator and runs the same
/// <c>ApprovalService</c> CAS the cockpit uses (first channel wins).
///
/// Raw <see cref="HttpClient"/> POST of JSON — a Slack incoming webhook answers a
/// plain <c>200 "ok"</c>. The URL is a secret (it embeds a token) and is NEVER
/// logged; failures surface only the status code.
/// </summary>
public sealed class SlackNotifier(HttpClient http, IOptions<SlackOptions> options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SlackOptions _options = options.Value;

    /// <summary>False when Slack:IncomingWebhookUrl is unset — callers treat posting as a no-op.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.IncomingWebhookUrl);

    public async Task PostActionAsync(OutboxAction action, string clientName, string reason, CancellationToken ct)
    {
        var body = BuildMessage(action, clientName, reason);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(_options.IncomingWebhookUrl, content, ct);

        if (!response.IsSuccessStatusCode)
            // No URL, no body: an incoming webhook URL embeds a secret token.
            throw new HttpRequestException(
                $"Slack incoming webhook returned {(int)response.StatusCode}.",
                inner: null, statusCode: response.StatusCode);
    }

    private static string BuildMessage(OutboxAction action, string clientName, string reason)
    {
        var idText = action.Id.ToString();
        var headline = $"*Action awaiting approval*\n*{action.ActionType}* for *{clientName}*";
        var detail = string.IsNullOrWhiteSpace(reason) ? headline : $"{headline}\n{reason}";

        var message = new
        {
            text = $"Action awaiting approval: {action.ActionType} for {clientName}",
            blocks = new object[]
            {
                new
                {
                    type = "section",
                    text = new { type = "mrkdwn", text = detail },
                },
                new
                {
                    type = "actions",
                    block_id = $"rd_action:{idText}",
                    elements = new object[]
                    {
                        new
                        {
                            type = "button",
                            action_id = "rd_approve",
                            style = "primary",
                            text = new { type = "plain_text", text = "Approve" },
                            value = idText,
                        },
                        new
                        {
                            type = "button",
                            action_id = "rd_dismiss",
                            style = "danger",
                            text = new { type = "plain_text", text = "Dismiss" },
                            value = idText,
                        },
                    },
                },
            },
        };

        return JsonSerializer.Serialize(message, Json);
    }
}
