using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using RD.Domain;

namespace RD.Infrastructure.Slack;

public enum SlackSignatureVerification
{
    Valid,
    BadSignature,
    TimestampOutOfTolerance,
}

/// <summary>
/// Verifies the inbound-authenticity of a Slack interactivity callback BEFORE any
/// processing. Slack signs each request as
/// <c>v0=HMAC-SHA256(signingSecret, "v0:{X-Slack-Request-Timestamp}:{rawBody}")</c>.
/// We recompute the HMAC over the EXACT raw body bytes, constant-time compare the
/// <c>X-Slack-Signature</c> header, then — because the timestamp is itself part of
/// the signed string — enforce the 5-minute replay window on that now-authentic
/// timestamp so a captured-and-replayed callback is rejected.
///
/// Mirrors <c>StripeSignatureVerifier</c>: pure and deterministic, the only ambient
/// input is the injected <see cref="IClock"/> so tests pin <c>now</c> and precompute
/// the expected v0 signature. The signing secret and the raw signature are NEVER
/// written to logs or exceptions.
/// </summary>
public sealed class SlackSignatureVerifier(IClock clock, IOptions<SlackOptions> options)
{
    /// <summary>Slack's fixed replay window: reject callbacks whose timestamp skews further than this.</summary>
    public const int ToleranceSeconds = 300;

    private const string Version = "v0";

    private readonly SlackOptions _options = options.Value;

    /// <summary>Verifies against the configured signing secret + the pinned 5-minute window.</summary>
    public SlackSignatureVerification Verify(string rawBody, string? timestampHeader, string? signatureHeader) =>
        Verify(rawBody, timestampHeader, signatureHeader, _options.SigningSecret, ToleranceSeconds);

    /// <summary>Explicit-secret overload — keeps the crypto core trivially testable.</summary>
    public SlackSignatureVerification Verify(
        string rawBody, string? timestampHeader, string? signatureHeader, string signingSecret, int toleranceSeconds)
    {
        if (string.IsNullOrEmpty(signingSecret)
            || string.IsNullOrWhiteSpace(signatureHeader)
            || string.IsNullOrWhiteSpace(timestampHeader)
            || !signatureHeader.StartsWith(Version + "=", StringComparison.Ordinal)
            || !long.TryParse(timestampHeader, out var timestamp))
            return SlackSignatureVerification.BadSignature;

        // The signed base string uses the RAW timestamp header text (byte-exact),
        // not a re-serialized long.
        var expected = ComputeSignature(signingSecret, timestampHeader, rawBody);
        var providedHex = signatureHeader[(Version.Length + 1)..];
        if (!FixedTimeHexEquals(expected, providedHex))
            return SlackSignatureVerification.BadSignature;

        // A valid signature guarantees the timestamp was not tampered (it is part of
        // the signed string) — now enforce the replay window on that authentic value.
        var skewSeconds = Math.Abs(clock.UtcNow.ToUnixTimeSeconds() - timestamp);
        return skewSeconds > toleranceSeconds
            ? SlackSignatureVerification.TimestampOutOfTolerance
            : SlackSignatureVerification.Valid;
    }

    private static byte[] ComputeSignature(string secret, string timestampRaw, string rawBody)
    {
        var baseString = Encoding.UTF8.GetBytes($"{Version}:{timestampRaw}:{rawBody}");
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return hmac.ComputeHash(baseString);
    }

    private static bool FixedTimeHexEquals(byte[] expected, string providedHex)
    {
        var provided = FromHex(providedHex);
        return provided is not null && CryptographicOperations.FixedTimeEquals(expected, provided);
    }

    private static byte[]? FromHex(string hex)
    {
        if (hex.Length % 2 != 0) return null;
        try
        {
            return Convert.FromHexString(hex); // case-insensitive; Slack emits lowercase
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
