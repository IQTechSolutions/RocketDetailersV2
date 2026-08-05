namespace RD.Infrastructure.Email;

/// <summary>
/// Config section "Email" — the SMTP relay used for transactional mail (today:
/// password-reset links). <see cref="Host"/> empty ⇒ email is not configured;
/// <see cref="IEmailSender.IsConfigured"/> is false and callers say so out loud
/// rather than dropping mail on the floor.
///
/// UserName/Password are secrets — configuration only, NEVER committed and NEVER
/// logged (the sender logs host/port/recipient, never credentials or bodies).
///
/// Keys:
///   Email:Host          — SMTP relay host (empty ⇒ email disabled)
///   Email:Port          — default 587
///   Email:UseStartTls   — default true; turn off only for a loopback catcher
///   Email:UserName      — SMTP auth user (empty ⇒ anonymous relay)
///   Email:Password      — SMTP auth password (secret)
///   Email:FromAddress   — the From address; required when Host is set
///   Email:FromName      — optional display name
///   Email:TimeoutSeconds— default 30
/// </summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>SMTP relay host. Empty ⇒ email is disabled and every send is refused.</summary>
    public string Host { get; set; } = "";

    /// <summary>587 is the submission port; implicit-TLS 465 is not supported by the BCL client.</summary>
    public int Port { get; set; } = 587;

    /// <summary>STARTTLS upgrade after connect. Leave on for anything but a local mail catcher.</summary>
    public bool UseStartTls { get; set; } = true;

    /// <summary>SMTP auth user. Empty ⇒ connect anonymously (internal relays often allow this).</summary>
    public string UserName { get; set; } = "";

    /// <summary>SMTP auth password. Read from configuration only; never logged.</summary>
    public string Password { get; set; } = "";

    /// <summary>The From address. Required once <see cref="Host"/> is set.</summary>
    public string FromAddress { get; set; } = "";

    /// <summary>Optional From display name.</summary>
    public string? FromName { get; set; }

    /// <summary>Send timeout. A wedged relay must not hold a request thread open indefinitely.</summary>
    public int TimeoutSeconds { get; set; } = 30;
}
