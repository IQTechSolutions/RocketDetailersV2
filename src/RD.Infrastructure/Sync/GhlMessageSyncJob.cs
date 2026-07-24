using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RD.Domain;
using RD.Domain.Entities;
using RD.Infrastructure.Gateways;

namespace RD.Infrastructure.Sync;

/// <summary>
/// Sweeps recent GHL conversations per configured location and upserts
/// OUTBOUND messages into GhlMessageProj — the raw material for F2 delivery
/// verification ("the dunning window advances only on verified sends").
/// Inbound messages are ignored. Same complete-sweep SyncRun semantics as the
/// other sync jobs; a failure in either location fails the whole run.
/// </summary>
public sealed class GhlMessageSyncJob(
    IDbContextFactory<RdDbContext> dbFactory,
    IGhlGateway ghl,
    IOptions<GhlOptions> options,
    IClock clock,
    ILogger<GhlMessageSyncJob> logger)
{
    private const int BodyPreviewMaxLength = 500;
    private const int MessageTypeMaxLength = 30; // column width on GhlMessageProj

    public async Task RunAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var run = new SyncRun { System = ExternalSystem.Ghl, StartedAt = clock.UtcNow };
        db.SyncRuns.Add(run);
        await db.SaveChangesAsync(ct);

        try
        {
            run.ItemsSeen = await SweepAsync(db, ct);
            run.Status = SyncRunStatus.Completed;
            run.CompletedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GHL message sync sweep failed; SyncRun {SyncRunId} marked Failed", run.Id);
            await SyncUtil.RecordFailureAsync(db, run, ex, logger);
        }
    }

    private async Task<int> SweepAsync(RdDbContext db, CancellationToken ct)
    {
        var o = options.Value;
        var now = clock.UtcNow;

        var contactToClient = (await db.IdentityLinks
                .Where(l => l.System == ExternalSystem.Ghl && l.Kind == LinkKind.Contact && l.InvalidatedAt == null)
                .Select(l => new { l.ExternalId, l.ClientId })
                .ToListAsync(ct))
            .ToDictionary(l => l.ExternalId, l => l.ClientId);

        // Gather outbound messages across all configured locations first…
        var swept = new List<(string LocationId, string ContactId, GhlMessageDto Message)>();
        foreach (var location in o.Locations)
        {
            var conversations = await ghl.SearchConversationsAsync(
                location.LocationId, o.ConversationSweepLimit, ct);

            foreach (var conversation in conversations)
            {
                var messages = await ghl.GetMessagesAsync(location.LocationId, conversation.Id, ct);
                foreach (var message in messages)
                {
                    if (!string.Equals(message.Direction, "outbound", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var contactId = message.ContactId ?? conversation.ContactId;
                    if (string.IsNullOrEmpty(contactId))
                        continue; // no contact → nothing to verify delivery against

                    swept.Add((location.LocationId, contactId, message));
                }
            }
        }

        // …then upsert in one pass against the existing rows for those ids.
        var messageIds = swept.Select(s => s.Message.Id).Distinct().ToList();
        var projections = await db.GhlMessages
            .Where(m => messageIds.Contains(m.MessageId))
            .ToDictionaryAsync(m => m.MessageId, ct);

        foreach (var (locationId, contactId, message) in swept)
        {
            if (!projections.TryGetValue(message.Id, out var proj))
            {
                proj = new GhlMessageProj
                {
                    MessageId = message.Id,
                    LocationId = locationId,
                    ContactId = contactId,
                    MessageType = message.MessageType,
                };
                projections[message.Id] = proj;
                db.GhlMessages.Add(proj);
            }
            proj.LocationId = locationId;
            proj.ContactId = contactId;
            proj.MessageType = SyncUtil.Truncate(message.MessageType, MessageTypeMaxLength);
            proj.BodyPreview = message.Body is { } body ? SyncUtil.Truncate(body, BodyPreviewMaxLength) : null;
            proj.SentAt = message.DateAdded;
            proj.ClientId = contactToClient.TryGetValue(contactId, out var clientId) ? clientId : null;
            proj.SourceSyncedAt = now;
        }

        await db.SaveChangesAsync(ct);
        return swept.Count;
    }
}
