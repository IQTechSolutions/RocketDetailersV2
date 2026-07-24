using FluentAssertions;
using Microsoft.Extensions.Options;
using RD.Infrastructure.Gateways;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace RD.Tests.Integration;

/// <summary>The TestMode gate lives inside the gateway: under it, no send can reach a real client.</summary>
public sealed class GhlSafetyTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();

    private GhlGateway Build(SafetyOptions safety)
    {
        var options = Options.Create(new GhlOptions
        {
            BaseUrl = _server.Urls[0],
            Locations =
            [
                new GhlLocationOptions { LocationId = "real_loc", Token = "pit_real" },
                new GhlLocationOptions { LocationId = "test_loc", Token = "pit_test" },
            ],
        });
        return new GhlGateway(new HttpClient(), options, Options.Create(safety),
            new RetryHelper { BaseDelay = TimeSpan.FromMilliseconds(1) });
    }

    [Fact]
    public async Task TestMode_redirects_every_send_to_the_test_contact()
    {
        _server.Given(Request.Create().WithPath("/contacts/*").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        var gw = Build(new SafetyOptions { GhlTestMode = true, TestContactLocationId = "test_loc", TestContactId = "test_contact" });
        var result = await gw.SetContactFieldAsync("real_loc", "real_contact", "hosted_invoice_url", "https://pay/x", default);

        result.Redirected.Should().BeTrue();
        result.EffectiveContactId.Should().Be("test_contact");
        // The write went to the TEST contact's path, never the real client's.
        _server.LogEntries.Should().Contain(e => e.RequestMessage.Path == "/contacts/test_contact");
        _server.LogEntries.Should().NotContain(e => e.RequestMessage.Path == "/contacts/real_contact");
    }

    [Fact]
    public async Task TestMode_without_a_configured_test_contact_refuses_to_send()
    {
        var gw = Build(new SafetyOptions { GhlTestMode = true });
        var act = () => gw.SetContactFieldAsync("real_loc", "real_contact", "f", "v", default);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    public void Dispose() => _server.Stop();
}
