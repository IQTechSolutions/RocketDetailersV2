using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Options;
using RD.Domain;
using RD.Infrastructure.Webhooks;

namespace RD.Tests.Webhooks;

public sealed class StripeSignatureVerifierTests
{
    private const string Secret = "whsec_testsecret_do_not_commit";
    private const string Payload = """{"id":"evt_1","type":"invoice.paid"}""";

    // Fixed clock: verification is deterministic, never wall-clock dependent.
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    private static StripeSignatureVerifier CreateVerifier(int toleranceSeconds = 300) =>
        new(new FixedClock(Now),
            Options.Create(new StripeWebhookOptions { SigningSecret = Secret, ToleranceSeconds = toleranceSeconds }));

    [Fact]
    public void ValidSignature_AtCurrentTime_Passes()
    {
        var timestamp = Now.ToUnixTimeSeconds();
        var header = SignedHeader(Payload, timestamp, Secret);

        CreateVerifier().Verify(Payload, header)
            .Should().Be(StripeSignatureVerification.Valid);
    }

    [Fact]
    public void ValidSignature_ToleratesKeyRotation_MultipleV1Values()
    {
        var timestamp = Now.ToUnixTimeSeconds();
        var goodSig = ComputeHex(Payload, timestamp, Secret);
        // Old + new signing key both present during rotation; the good one must match.
        var header = $"t={timestamp},v1=deadbeef,v1={goodSig}";

        CreateVerifier().Verify(Payload, header)
            .Should().Be(StripeSignatureVerification.Valid);
    }

    [Fact]
    public void TamperedPayload_Fails()
    {
        var timestamp = Now.ToUnixTimeSeconds();
        var header = SignedHeader(Payload, timestamp, Secret);

        CreateVerifier().Verify(Payload + " tampered", header)
            .Should().Be(StripeSignatureVerification.BadSignature);
    }

    [Fact]
    public void WrongSecret_Fails()
    {
        var timestamp = Now.ToUnixTimeSeconds();
        var header = SignedHeader(Payload, timestamp, "whsec_a_different_secret");

        CreateVerifier().Verify(Payload, header)
            .Should().Be(StripeSignatureVerification.BadSignature);
    }

    [Fact]
    public void TimestampOlderThanTolerance_Fails_EvenWithValidSignature()
    {
        // Correctly signed, but 301s in the past against a 300s tolerance.
        var timestamp = Now.ToUnixTimeSeconds() - 301;
        var header = SignedHeader(Payload, timestamp, Secret);

        CreateVerifier(toleranceSeconds: 300).Verify(Payload, header)
            .Should().Be(StripeSignatureVerification.TimestampOutOfTolerance);
    }

    [Fact]
    public void TimestampInFutureBeyondTolerance_Fails()
    {
        var timestamp = Now.ToUnixTimeSeconds() + 301;
        var header = SignedHeader(Payload, timestamp, Secret);

        CreateVerifier(toleranceSeconds: 300).Verify(Payload, header)
            .Should().Be(StripeSignatureVerification.TimestampOutOfTolerance);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("v1=abc")]                        // no timestamp
    [InlineData("t=1700000000")]                  // no signature
    [InlineData("t=notanumber,v1=abc")]           // unparseable timestamp
    public void MalformedHeader_Fails(string header)
    {
        CreateVerifier().Verify(Payload, header)
            .Should().Be(StripeSignatureVerification.BadSignature);
    }

    [Fact]
    public void NullHeader_Fails()
    {
        CreateVerifier().Verify(Payload, null)
            .Should().Be(StripeSignatureVerification.BadSignature);
    }

    private static string SignedHeader(string payload, long timestamp, string secret) =>
        $"t={timestamp},v1={ComputeHex(payload, timestamp, secret)}";

    private static string ComputeHex(string payload, long timestamp, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{payload}"));
        return Convert.ToHexString(hash); // uppercase; verifier decodes case-insensitively
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
