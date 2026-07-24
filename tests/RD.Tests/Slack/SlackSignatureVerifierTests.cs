using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Options;
using RD.Domain;
using RD.Infrastructure.Slack;

namespace RD.Tests.Slack;

public sealed class SlackSignatureVerifierTests
{
    private const string Secret = "8f742231b10e8888abcd99yyyzzz85a5"; // fake signing secret — never a real one
    private const string Body = "payload=%7B%22type%22%3A%22block_actions%22%7D";

    // Fixed clock: verification is deterministic, never wall-clock dependent.
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    private static SlackSignatureVerifier CreateVerifier() =>
        new(new FixedClock(Now), Options.Create(new SlackOptions { SigningSecret = Secret }));

    [Fact]
    public void ValidSignature_AtCurrentTime_Passes()
    {
        var ts = Now.ToUnixTimeSeconds().ToString();
        var header = SignedHeader(Body, ts, Secret);

        CreateVerifier().Verify(Body, ts, header)
            .Should().Be(SlackSignatureVerification.Valid);
    }

    [Fact]
    public void TamperedBody_Fails()
    {
        var ts = Now.ToUnixTimeSeconds().ToString();
        var header = SignedHeader(Body, ts, Secret);

        CreateVerifier().Verify(Body + "&injected=1", ts, header)
            .Should().Be(SlackSignatureVerification.BadSignature);
    }

    [Fact]
    public void WrongSecret_Fails()
    {
        var ts = Now.ToUnixTimeSeconds().ToString();
        var header = SignedHeader(Body, ts, "a_different_secret");

        CreateVerifier().Verify(Body, ts, header)
            .Should().Be(SlackSignatureVerification.BadSignature);
    }

    [Fact]
    public void StaleTimestamp_Fails_EvenWithValidSignature()
    {
        // Correctly signed, but 301s old against the fixed 300s window.
        var ts = (Now.ToUnixTimeSeconds() - 301).ToString();
        var header = SignedHeader(Body, ts, Secret);

        CreateVerifier().Verify(Body, ts, header)
            .Should().Be(SlackSignatureVerification.TimestampOutOfTolerance);
    }

    [Fact]
    public void FutureTimestampBeyondWindow_Fails()
    {
        var ts = (Now.ToUnixTimeSeconds() + 301).ToString();
        var header = SignedHeader(Body, ts, Secret);

        CreateVerifier().Verify(Body, ts, header)
            .Should().Be(SlackSignatureVerification.TimestampOutOfTolerance);
    }

    [Fact]
    public void TimestampTampered_AfterSigning_Fails()
    {
        // Signature was computed over the real timestamp; the header claims a fresh one.
        var realTs = (Now.ToUnixTimeSeconds() - 10_000).ToString();
        var header = SignedHeader(Body, realTs, Secret);
        var forgedTs = Now.ToUnixTimeSeconds().ToString();

        CreateVerifier().Verify(Body, forgedTs, header)
            .Should().Be(SlackSignatureVerification.BadSignature);
    }

    [Theory]
    [InlineData("")]                     // empty signature
    [InlineData("garbage")]              // no v0= prefix
    [InlineData("v0=nothex")]            // v0 prefix but not hex
    [InlineData("v1=abcdef")]            // wrong version prefix
    public void MalformedSignatureHeader_Fails(string header)
    {
        var ts = Now.ToUnixTimeSeconds().ToString();
        CreateVerifier().Verify(Body, ts, header)
            .Should().Be(SlackSignatureVerification.BadSignature);
    }

    [Theory]
    [InlineData("")]              // empty timestamp
    [InlineData("notanumber")]   // unparseable timestamp
    public void MalformedTimestampHeader_Fails(string timestamp)
    {
        var header = SignedHeader(Body, Now.ToUnixTimeSeconds().ToString(), Secret);
        CreateVerifier().Verify(Body, timestamp, header)
            .Should().Be(SlackSignatureVerification.BadSignature);
    }

    [Fact]
    public void NullHeaders_Fail()
    {
        CreateVerifier().Verify(Body, null, null)
            .Should().Be(SlackSignatureVerification.BadSignature);
    }

    private static string SignedHeader(string body, string timestamp, string secret) =>
        "v0=" + ComputeHex(body, timestamp, secret);

    private static string ComputeHex(string body, string timestamp, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes($"v0:{timestamp}:{body}"));
        return Convert.ToHexString(hash).ToLowerInvariant(); // Slack emits lowercase; verifier decodes case-insensitively
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
