using FluentAssertions;
using Microsoft.Extensions.Options;
using RD.Infrastructure.Gateways;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace RD.Tests.Integration;

/// <summary>
/// The GHL tag primitives for the `close` write (Convert→Bill→Close, rung B). GetContactTags is the
/// read-before-write check; AddContactTag posts the tag and — crucially for a live-chain write — is
/// TestMode-redirected to the test contact so it can never fire a real client's onboarding chain.
/// </summary>
public sealed class GhlGatewayTagTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    private GhlGateway Build(SafetyOptions? safety = null)
    {
        var options = Options.Create(new GhlOptions
        {
            BaseUrl = _server.Urls[0],
            Locations =
            [
                new GhlLocationOptions { LocationId = "loc", Token = "pit" },
                new GhlLocationOptions { LocationId = "test_loc", Token = "pit_test" },
            ],
        });
        return new GhlGateway(new HttpClient(), options, Options.Create(safety ?? new SafetyOptions()),
            new RetryHelper { BaseDelay = TimeSpan.FromMilliseconds(1) });
    }

    [Fact]
    public async Task GetContactTags_reads_the_tags_array()
    {
        _server.Given(Request.Create().WithPath("/contacts/c1").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBodyAsJson(new { contact = new { id = "c1", tags = new[] { "trial", "close" } } }));

        var tags = await Build().GetContactTagsAsync("loc", "c1", CancellationToken.None);

        tags.Should().Contain("close").And.Contain("trial");
    }

    [Fact]
    public async Task AddContactTag_posts_the_tag()
    {
        _server.Given(Request.Create().WithPath("/contacts/c1/tags").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        var result = await Build(new SafetyOptions { GhlTestMode = false })
            .AddContactTagAsync("loc", "c1", "close", CancellationToken.None);

        result.Redirected.Should().BeFalse();
        (_server.LogEntries.Last().RequestMessage.Body ?? "").Should().Contain("close");
    }

    [Fact]
    public async Task AddContactTag_is_testmode_redirected_to_the_test_contact()
    {
        _server.Given(Request.Create().WithPath("/contacts/*/tags").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        var gw = Build(new SafetyOptions { GhlTestMode = true, TestContactLocationId = "test_loc", TestContactId = "test_contact" });
        var result = await gw.AddContactTagAsync("loc", "real_contact", "close", CancellationToken.None);

        result.Redirected.Should().BeTrue();
        result.EffectiveContactId.Should().Be("test_contact");
        _server.LogEntries.Should().Contain(e => e.RequestMessage.Path == "/contacts/test_contact/tags");
        _server.LogEntries.Should().NotContain(e => e.RequestMessage.Path == "/contacts/real_contact/tags");
    }
}
