using System.Text.Json;

namespace RD.Domain;

/// <summary>
/// Validates that a mapping-verification snapshot exactly matches the supplied
/// active enforcement links at their current versions. Missing, extra, stale,
/// malformed, or legacy-incomplete pins fail closed.
/// </summary>
public static class MappingVerificationCoverage
{
    public static bool PinsAll(
        string? verifiedLinksJson,
        IEnumerable<(Guid LinkId, int LinkVersion)> requiredLinks)
    {
        if (string.IsNullOrWhiteSpace(verifiedLinksJson)) return false;
        var required = requiredLinks.ToHashSet();
        if (required.Count == 0) return false;

        try
        {
            using var document = JsonDocument.Parse(verifiedLinksJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return false;

            var pins = new HashSet<(Guid LinkId, int LinkVersion)>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object
                    || !element.TryGetProperty("linkId", out var idElement)
                    || !idElement.TryGetGuid(out var linkId)
                    || !element.TryGetProperty("linkVersion", out var versionElement)
                    || !versionElement.TryGetInt32(out var linkVersion))
                    return false;
                pins.Add((linkId, linkVersion));
            }

            return pins.SetEquals(required);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
