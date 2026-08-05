using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RD.Infrastructure.Email;

/// <summary>
/// Transactional email. <see cref="IsConfigured"/> is the honest signal: when the
/// relay isn't configured, callers surface that to the user instead of pretending
/// a message was sent.
/// </summary>
public interface IEmailSender
{
    /// <summary>False when Email:Host is unset — <see cref="SendAsync"/> would throw.</summary>
    bool IsConfigured { get; }

    /// <summary>Sends one HTML message. Throws on relay failure; callers decide what the user sees.</summary>
    Task SendAsync(string toAddress, string subject, string htmlBody, CancellationToken ct = default);
}

/// <summary>
/// SMTP submission over the BCL <see cref="SmtpClient"/> — deliberately
/// dependency-free. This app sends a trickle of internal operator mail to a relay
/// that speaks STARTTLS on 587; that is exactly the shape the BCL client covers.
/// If a relay ever requires XOAUTH2 or implicit TLS on 465, swap this
/// implementation for MailKit — nothing outside this file changes.
/// </summary>
public sealed class SmtpEmailSender(IOptions<EmailOptions> options, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    private readonly EmailOptions _options = options.Value;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.Host)
                                && !string.IsNullOrWhiteSpace(_options.FromAddress);

    public async Task SendAsync(string toAddress, string subject, string htmlBody, CancellationToken ct = default)
    {
        if (!IsConfigured)
            throw new InvalidOperationException(
                "Email is not configured. Set Email:Host and Email:FromAddress before sending.");

        using var client = new SmtpClient(_options.Host, _options.Port)
        {
            EnableSsl = _options.UseStartTls,
            Timeout = _options.TimeoutSeconds * 1000,
        };
        if (!string.IsNullOrWhiteSpace(_options.UserName))
        {
            client.UseDefaultCredentials = false;
            client.Credentials = new NetworkCredential(_options.UserName, _options.Password);
        }

        using var message = new MailMessage
        {
            From = new MailAddress(_options.FromAddress, _options.FromName ?? _options.FromAddress),
            Subject = subject,
            Body = htmlBody,
            IsBodyHtml = true,
        };
        message.To.Add(toAddress);

        await client.SendMailAsync(message, ct);
        // Recipient + subject only: bodies carry reset links, which are credentials.
        logger.LogInformation("Sent email {Subject} to {Recipient} via {Host}:{Port}.",
            subject, toAddress, _options.Host, _options.Port);
    }
}
