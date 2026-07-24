namespace RD.Infrastructure.Slack;

/// <summary>
/// Config section "Slack". The signing secret and the incoming-webhook URL are
/// secrets — read from configuration only, NEVER committed and NEVER logged
/// (see <see cref="SlackSignatureVerifier"/>).
///
/// Keys:
///   Slack:IncomingWebhookUrl        — where the notifier POSTs Block Kit messages
///   Slack:SigningSecret             — verifies inbound interactivity callbacks
///   Slack:BotName                   — optional display name (informational)
///   Slack:UserMap:N:SlackUserId     — a Slack user id to link…
///   Slack:UserMap:N:Email           — …to this internal AppUser (seeds the slack:user_id claim)
/// </summary>
public sealed class SlackOptions
{
    public const string SectionName = "Slack";

    /// <summary>Incoming-webhook URL the notifier posts to. Empty ⇒ the notification job is a no-op.</summary>
    public string IncomingWebhookUrl { get; set; } = "";

    /// <summary>The Slack app signing secret used to verify interactivity callbacks. Read from configuration only.</summary>
    public string SigningSecret { get; set; } = "";

    /// <summary>Optional bot display name — informational only.</summary>
    public string? BotName { get; set; }

    /// <summary>Slack-user → internal-user links, applied as slack:user_id claims by <see cref="SlackUserSeeder"/>.</summary>
    public List<SlackUserMapEntry> UserMap { get; set; } = [];
}

/// <summary>One Slack-user → AppUser link. Binds from Slack:UserMap:N:SlackUserId / :Email.</summary>
public sealed class SlackUserMapEntry
{
    public string SlackUserId { get; set; } = "";
    public string Email { get; set; } = "";
}

/// <summary>Claim type carrying a linked Slack user id on an AppUser. OV item 15: a Slack signature authenticates the workspace, never a person.</summary>
public static class SlackClaims
{
    public const string UserId = "slack:user_id";
}
