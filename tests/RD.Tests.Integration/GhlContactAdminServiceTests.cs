using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RD.Domain;
using RD.Domain.Entities;
using RD.Infrastructure.Enforcement;
using RD.Infrastructure.Gateways;
using RD.Tests.Integration.TestInfra;
using RD.Web.Services;

namespace RD.Tests.Integration;

public sealed class GhlContactAdminServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 10, 0, 0, TimeSpan.Zero);
    private readonly SyncTestDb _db = new();
    private readonly RecordingGhlGateway _gateway = new();

    [Fact]
    public async Task CreateAndLink_reloads_after_the_fence_and_rejects_a_client_retired_while_waiting()
    {
        var survivorId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        await using (var seed = _db.CreateContext())
        {
            seed.Clients.AddRange(Client(survivorId, "Survivor"), Client(clientId, "About to retire"));
            await seed.SaveChangesAsync();
        }

        var options = Options.Create(new GhlOptions
        {
            Locations =
            [
                new GhlLocationOptions
                {
                    LocationId = "location_1",
                    Token = "pit_test_dummy",
                },
            ],
        });
        var service = new GhlContactAdminService(_db.Factory, _gateway, options, new TestClock(Now));

        await using var mergeDb = _db.CreateContext();
        var mergeFence = await ClientMutationFence.AcquireAsync(mergeDb, clientId);
        Task<GhlContactWriteResult>? createTask = null;
        try
        {
            createTask = service.CreateAndLinkAsync(clientId, "location_1", "operator");
            var first = await Task.WhenAny(createTask, Task.Delay(TimeSpan.FromMilliseconds(200)));
            first.Should().NotBe(createTask, "GHL linking must wait for a merge holding the client fence");

            var client = await mergeDb.Clients.SingleAsync(c => c.Id == clientId);
            client.MergedIntoClientId = survivorId;
            client.MergedAt = Now;
            await mergeDb.SaveChangesAsync();
        }
        finally
        {
            await mergeFence.DisposeAsync();
        }

        var result = await createTask!;
        result.Ok.Should().BeFalse();
        result.Message.Should().ContainEquivalentOf("merged");
        _gateway.CreateCalls.Should().Be(0);

        await using var verify = _db.CreateContext();
        (await verify.IdentityLinks.CountAsync(link => link.ClientId == clientId)).Should().Be(0);
    }

    private static Client Client(Guid id, string name) => new()
    {
        Id = id,
        BusinessName = name,
        Email = $"{id:N}@example.test",
        ContractType = ContractType.Paid,
        AccountType = AccountType.Master,
        CreatedAt = Now,
    };

    public void Dispose() => _db.Dispose();

    private sealed class RecordingGhlGateway : IGhlGateway
    {
        public int CreateCalls { get; private set; }

        public Task<string> CreateContactAsync(
            string locationId,
            string token,
            string? email,
            string name,
            CancellationToken ct)
        {
            CreateCalls++;
            return Task.FromResult("contact_created");
        }

        public Task<IReadOnlyList<GhlConversationDto>> SearchConversationsAsync(
            string locationId,
            int limit,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<GhlMessageDto>> GetMessagesAsync(
            string locationId,
            string conversationId,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<GhlContactDto>> SearchContactsAsync(
            string? locationId,
            string token,
            string query,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<GhlWriteResult> SetContactFieldAsync(
            string locationId,
            string contactId,
            string fieldKey,
            string value,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<GhlWriteResult> TriggerWorkflowAsync(
            string locationId,
            string contactId,
            string workflowId,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> GetContactTagsAsync(
            string locationId,
            string contactId,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<GhlWriteResult> AddContactTagAsync(
            string locationId,
            string contactId,
            string tag,
            CancellationToken ct) => throw new NotSupportedException();
    }
}
