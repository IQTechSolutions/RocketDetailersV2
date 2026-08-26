using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Options;
using RD.Infrastructure.Gateways;

namespace RD.Tests.Integration;

public sealed class MetaActivityReaderTests
{
    [Fact]
    public async Task Campaign_activity_sweep_is_GET_only_paginated_and_status_normalized()
    {
        var handler = new GetOnlySequenceHandler(
            """
            {
              "data": [
                {
                  "event_time": "2026-08-24T10:15:00+0000",
                  "event_type": "update_campaign_run_status",
                  "object_id": "camp_1",
                  "object_name": "Client One",
                  "object_type": "CAMPAIGN",
                  "actor_id": "actor_1",
                  "actor_name": "Operator",
                  "extra_data": "{\"old_value\":\"Active\",\"new_value\":\"Inactive\"}"
                },
                {
                  "event_time": "2026-08-24T10:16:00+0000",
                  "event_type": "update_ad_run_status",
                  "object_id": "ad_ignored",
                  "extra_data": "{\"old_value\":\"Active\",\"new_value\":\"Inactive\"}"
                }
              ],
              "paging": {
                "cursors": { "after": "cursor_1" },
                "next": "https://graph.facebook.test/activities?access_token=must_not_be_followed"
              }
            }
            """,
            """
            {
              "data": [
                {
                  "event_time": 1787567400,
                  "event_type": "update_campaign_run_status",
                  "object_id": "camp_2",
                  "extra_data": { "old_value": "Paused", "new_value": "Active" }
                }
              ]
            }
            """);
        var options = Options.Create(new MetaOptions
        {
            AccessToken = "meta_test_token",
            BaseUrl = "https://graph.facebook.test/v25.0",
        });
        var reader = new MetaActivityReader(
            new HttpClient(handler),
            options,
            new RetryHelper { BaseDelay = TimeSpan.Zero });

        var rows = await reader.ListCampaignStatusActivitiesAsync(
            "1234",
            new DateTimeOffset(2026, 8, 24, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 25, 0, 0, 0, TimeSpan.Zero),
            CancellationToken.None);

        rows.Should().HaveCount(2);
        rows[0].ObjectId.Should().Be("camp_1");
        rows[0].OldStatus.Should().Be("ACTIVE");
        rows[0].NewStatus.Should().Be("PAUSED");
        rows[1].ObjectId.Should().Be("camp_2");
        rows[1].OldStatus.Should().Be("PAUSED");
        rows[1].NewStatus.Should().Be("ACTIVE");

        handler.Requests.Should().HaveCount(2);
        handler.Requests.Should().OnlyContain(request => request.Method == HttpMethod.Get);
        handler.Requests.Should().OnlyContain(request =>
            request.Authorization == "Bearer meta_test_token"
            && !request.Uri.Contains("meta_test_token", StringComparison.Ordinal)
            && !request.Uri.Contains("must_not_be_followed", StringComparison.Ordinal));
        handler.Requests[0].Uri.Should().Contain("/act_1234/activities");
        handler.Requests[0].Uri.Should().Contain("category=STATUS");
        handler.Requests[1].Uri.Should().Contain("after=cursor_1");
    }

    private sealed class GetOnlySequenceHandler(params string[] responses) : HttpMessageHandler
    {
        private int _index;
        public List<CapturedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Get)
                throw new InvalidOperationException($"Shadow comparison attempted forbidden HTTP method {request.Method}.");
            if (_index >= responses.Length)
                throw new InvalidOperationException("Reader requested more pages than the test supplied.");

            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri?.AbsoluteUri ?? "",
                request.Headers.Authorization?.ToString()));
            var body = responses[_index++];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed record CapturedRequest(HttpMethod Method, string Uri, string? Authorization);
}
