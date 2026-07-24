using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RD.Domain;
using RD.Domain.Entities;
using RD.Infrastructure;
using RD.Infrastructure.Gateways;

namespace RD.Web.Services;

/// <summary>Result of a GHL create — success flag plus a human sentence for the snackbar.</summary>
public sealed record GhlContactWriteResult(bool Ok, string Message);

/// <summary>
/// Admin action: create a GHL contact for a client that has none (the comms
/// channel every client needs) and link it. GHL dedupes on email, so if a contact
/// already exists it is linked rather than duplicated. The location is the admin's
/// choice. A genuine write to GHL — gated behind the Operator role at the call site.
/// </summary>
public class GhlContactAdminService(
    IDbContextFactory<RdDbContext> factory,
    IGhlGateway ghl,
    IOptions<GhlOptions> ghlOptions,
    IClock clock)
{
    private const string DefaultActor = "operator";

    /// <summary>The configured GHL locations (for the picker). Name falls back to the id.</summary>
    public IReadOnlyList<(string LocationId, string Label)> Locations() =>
        ghlOptions.Value.Locations
            .Where(l => !string.IsNullOrWhiteSpace(l.LocationId) && !string.IsNullOrWhiteSpace(l.Token))
            .Select(l => (l.LocationId, string.IsNullOrWhiteSpace(l.Name) ? l.LocationId : l.Name!))
            .ToList();

    public async Task<GhlContactWriteResult> CreateAndLinkAsync(
        Guid clientId, string locationId, string actor = DefaultActor, CancellationToken ct = default)
    {
        var location = ghlOptions.Value.Locations.FirstOrDefault(l => l.LocationId == locationId);
        if (location is null || string.IsNullOrWhiteSpace(location.Token))
            return new GhlContactWriteResult(false, "That GHL location isn't configured (no token).");

        await using var db = await factory.CreateDbContextAsync(ct);
        var now = clock.UtcNow;

        var client = await db.Clients.FirstOrDefaultAsync(c => c.Id == clientId, ct);
        if (client is null) return new GhlContactWriteResult(false, "Client not found.");

        var already = await db.IdentityLinks.AnyAsync(l =>
            l.ClientId == clientId && l.System == ExternalSystem.Ghl && l.Kind == LinkKind.Contact && l.InvalidatedAt == null, ct);
        if (already) return new GhlContactWriteResult(false, "This client already has a GHL contact.");

        string contactId;
        try
        {
            contactId = await ghl.CreateContactAsync(locationId, location.Token, client.Email, client.BusinessName, ct);
        }
        catch (Exception ex)
        {
            return new GhlContactWriteResult(false, $"GHL create failed: {ex.Message}");
        }

        // GHL may have deduped to an existing contact that another client already owns.
        var ownedByOther = await db.IdentityLinks.AnyAsync(l =>
            l.System == ExternalSystem.Ghl && l.Kind == LinkKind.Contact && l.ExternalId == contactId && l.ClientId != clientId, ct);
        if (ownedByOther)
            return new GhlContactWriteResult(false, $"GHL returned contact {contactId}, but it's already linked to another client — resolve that first.");

        db.IdentityLinks.Add(new IdentityLink
        {
            Id = Guid.NewGuid(), ClientId = clientId, System = ExternalSystem.Ghl,
            Kind = LinkKind.Contact, ExternalId = contactId, CreatedAt = now,
        });
        await db.SaveChangesAsync(ct);

        return new GhlContactWriteResult(true, $"GHL contact created and linked ({contactId}).");
    }
}
