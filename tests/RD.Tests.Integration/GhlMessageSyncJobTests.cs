using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RD.Domain;
using RD.Infrastructure.Gateways;
using RD.Infrastructure.Sync;
using RD.Tests.Integration.TestInfra;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace RD.Tests.Integration;

public sealed class GhlMessageSyncJobTests : IDisposable
{
    private readonly SyncTestDb _db = new();
    private readonly WireMockServer _server = WireMockServer.Start();
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        _server.Stop();
        _server.Dispose();
        _db.Dispose();
    }

    private GhlMessageSyncJob CreateJob()
    {
        var options = Options.Create(new GhlOptions
        {
            BaseUrl = _server.Urls[0],
            ConversationSweepLimit = 50,
            Locations = [new GhlLocationOptions { LocationId = "loc_1", Token = "pit_test_token" }],
        });
        var gateway = new GhlGateway(
            new HttpClient(), options, new RetryHelper { BaseDelay = TimeSpan.FromMilliseconds(1) });
        return new GhlMessageSyncJob(_db.Factory, gateway, options, _clock, NullLogger<GhlMessageSyncJob>.Instance);
    }

    [Fact]
    public async Task Sweep_RunTwice_UpsertsOutboundMessagesOnly_TruncatesBody()
    {
        var clientId = _db.SeedClientWithLink(ExternalSystem.Ghl, LinkKind.Contact, "cont_1");

        _server.Given(Request.Create().WithPath("/conversations/search")
                .WithParam("locationId", "loc_1").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""
                {
                  "conversations": [
                    { "id": "conv_1", "contactId": "cont_1", "locationId": "loc_1" }
                  ],
                  "total": 1
                }
                """));

        var longBody = new string('x', 600);
        _server.Given(Request.Create().WithPath("/conversations/conv_1/messages").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody($$"""
                {
                  "messages": {
                    "lastMessageId": "msg_in",
                    "nextPage": false,
                    "messages": [
                      {
                        "id": "msg_out", "direction": "outbound", "messageType": "TYPE_SMS",
                        "body": "{{longBody}}", "contactId": "cont_1",
                        "dateAdded": "2026-07-24T09:30:00.000Z"
                      },
                      {
                        "id": "msg_in", "direction": "inbound", "messageType": "TYPE_SMS",
                        "body": "customer reply", "contactId": "cont_1",
                        "dateAdded": "2026-07-24T10:00:00.000Z"
                      }
                    ]
                  }
                }
                """));

        var job = CreateJob();
        await job.RunAsync(CancellationToken.None);
        await job.RunAsync(CancellationToken.None); // idempotency proof

        await using var db = _db.CreateContext();

        // Outbound only, upserted once across two runs.
        var messages = db.GhlMessages.ToList();
        messages.Should().HaveCount(1);
        var message = messages[0];
        message.MessageId.Should().Be("msg_out");
        message.LocationId.Should().Be("loc_1");
        message.ContactId.Should().Be("cont_1");
        message.MessageType.Should().Be("TYPE_SMS");
        message.BodyPreview.Should().HaveLength(500); // truncated from 600
        message.SentAt.Should().Be(new DateTimeOffset(2026, 7, 24, 9, 30, 0, TimeSpan.Zero));
        message.ClientId.Should().Be(clientId);

        var runs = db.SyncRuns.ToList();
        runs.Should().HaveCount(2);
        runs.Should().OnlyContain(r =>
            r.System == ExternalSystem.Ghl
            && r.Status == SyncRunStatus.Completed
            && r.CompletedAt != null
            && r.ItemsSeen == 1);

        // Per-location bearer token + pinned Version header on every request.
        _server.LogEntries.Should().OnlyContain(e =>
            e.Header("Authorization") == "Bearer pit_test_token"
            && e.Header("Version") == GhlGateway.ApiVersion);
    }
}
