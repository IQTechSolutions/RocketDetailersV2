using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using RD.Domain;

namespace RD.Infrastructure.Webhooks;

public enum StripeSignatureVerification
{
    Valid,
    BadSignature,
    TimestampOutOfTolerance,
}

/// <summary>
/// Verifies the inbound-authenticity of a Stripe webhook BEFORE any processing:
/// the `Stripe-Signature` header carries `t=&lt;unix&gt;,v1=&lt;hex-hmac&gt;` (possibly
/// several v1 values during signing-key rotation). We recompute HMAC-SHA256 over
/// `"{t}.{payload}"` with the endpoint secret, constant-time compare, then enforce
/// the pinned timestamp tolerance so a captured-and-replayed body is rejected.
///
/// Pure and deterministic: the only ambient input is the injected IClock, so tests
/// pin `now` and precompute the expected signature. The signing secret and the raw
/// v1 signature are NEVER written to logs or exceptions.
/// </summary>
public sealed class StripeSignatureVerifier(IClock clock, IOptions<StripeWebhookOptions> options)
{
    private readonly StripeWebhookOptions _options = options.Value;

    /// <summary>Verifies against the configured secret + tolerance.</summary>
    public StripeSignatureVerification Verify(string payload, string? signatureHeader) =>
        Verify(payload, signatureHeader, _options.SigningSecret, _options.ToleranceSeconds);

    /// <summary>Explicit-secret overload — keeps the crypto core trivially testable.</summary>
    public StripeSignatureVerification Verify(
        string payload, string? signatureHeader, string signingSecret, int toleranceSeconds)
    {
        if (string.IsNullOrEmpty(signingSecret) || string.IsNullOrWhiteSpace(signatureHeader))
            return StripeSignatureVerification.BadSignature;

        long? timestamp = null;
        var signatures = new List<string>();
        foreach (var element in signatureHeader.Split(','))
        {
            var separator = element.IndexOf('=');
            if (separator <= 0) continue;
            var key = element[..separator].Trim();
            var value = element[(separator + 1)..].Trim();
            if (key == "t")
            {
                if (long.TryParse(value, out var parsed)) timestamp = parsed;
            }
            else if (key == "v1" && value.Length > 0)
            {
                signatures.Add(value);
            }
        }

        // A header we cannot parse into (t, v1) is, functionally, a bad signature.
        if (timestamp is null || signatures.Count == 0)
            return StripeSignatureVerification.BadSignature;

        var expected = ComputeSignature(signingSecret, timestamp.Value, payload);
        var matched = false;
        foreach (var candidate in signatures)
        {
            // Deliberately no early-out: check every candidate so timing does not
            // leak how many signatures matched.
            if (FixedTimeHexEquals(expected, candidate)) matched = true;
        }
        if (!matched)
            return StripeSignatureVerification.BadSignature;

        // Signature authenticates `t` itself (it is part of the signed string), so a
        // valid signature guarantees the timestamp was not tampered — now enforce the
        // replay window on that authentic timestamp.
        var skewSeconds = Math.Abs(clock.UtcNow.ToUnixTimeSeconds() - timestamp.Value);
        return skewSeconds > toleranceSeconds
            ? StripeSignatureVerification.TimestampOutOfTolerance
            : StripeSignatureVerification.Valid;
    }

    private static byte[] ComputeSignature(string secret, long timestamp, string payload)
    {
        var signedPayload = Encoding.UTF8.GetBytes($"{timestamp}.{payload}");
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return hmac.ComputeHash(signedPayload);
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
            return Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
