using Microsoft.EntityFrameworkCore;
using RD.Domain;
using RD.Domain.Entities;
using RD.Infrastructure;

namespace RD.Web.Services;

/// <summary>A package with its current (latest effective) version summarized for the price-book grid.</summary>
public sealed record PackageAdminRow(
    Guid Id, string Name, bool IsActive, string? OfferName, string? StripePriceId,
    decimal DailyRate, decimal DailyBudget, decimal? TrialSpendCap, string CurrencyCode, int VersionCount);

/// <summary>Result of a package write — success flag plus a human sentence for the snackbar.</summary>
public sealed record PackageWriteResult(bool Ok, string Message);

/// <summary>
/// Admin writes for the price book (the existing Package / PackageVersion system). Setting a
/// price adds a NEW effective-dated PackageVersion — edits never rewrite history, matching how
/// ledger and decisions snapshot the version they used. The StripePriceId on the effective
/// version is what the Convert→Bill→Close draft (A1) bills the own-account service fee against.
/// </summary>
public class PackageAdminService(IDbContextFactory<RdDbContext> factory, IClock clock)
{
    private const string DefaultActor = "operator";

    public async Task<List<PackageAdminRow>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Packages.AsNoTracking()
            .OrderBy(p => p.Name)
            .Select(p => new
            {
                p.Id, p.Name, p.IsActive,
                Latest = p.Versions.OrderByDescending(v => v.EffectiveFrom).FirstOrDefault(),
                Count = p.Versions.Count,
            })
            .ToListAsync(ct);

        return rows.Select(p => new PackageAdminRow(
            p.Id, p.Name, p.IsActive,
            p.Latest?.OfferName,
            p.Latest?.StripePriceId,
            p.Latest?.DailyRate ?? 0m,
            p.Latest?.DailyBudget ?? 0m,
            p.Latest?.TrialSpendCap,
            p.Latest?.CurrencyCode ?? "USD",
            p.Count)).ToList();
    }

    public async Task<PackageWriteResult> CreatePackageAsync(string name, CancellationToken ct = default)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return new(false, "Package name is required.");

        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.Packages.AnyAsync(p => p.Name == name, ct))
            return new(false, $"A package named \"{name}\" already exists.");

        db.Packages.Add(new Package { Id = Guid.NewGuid(), Name = name, IsActive = true });
        await db.SaveChangesAsync(ct);
        return new(true, $"Package \"{name}\" created — set a price to make it billable.");
    }

    /// <summary>
    /// Add a new effective-dated version carrying the Stripe price and terms. Never rewrites the
    /// prior version — the effective-from is "now", so the new terms take over going forward while
    /// history stays intact.
    /// </summary>
    public async Task<PackageWriteResult> AddVersionAsync(
        Guid packageId, string? stripePriceId, string? offerName,
        decimal dailyRate, decimal dailyBudget, decimal? trialSpendCap, string currencyCode,
        string? actor = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (!await db.Packages.AnyAsync(p => p.Id == packageId, ct))
            return new(false, "Package not found (maybe it changed in another tab).");

        currencyCode = string.IsNullOrWhiteSpace(currencyCode) ? "USD" : currencyCode.Trim().ToUpperInvariant();
        if (currencyCode.Length != 3)
            return new(false, "Currency must be a 3-letter ISO code (e.g. USD).");

        db.PackageVersions.Add(new PackageVersion
        {
            Id = Guid.NewGuid(),
            PackageId = packageId,
            EffectiveFrom = clock.UtcNow,
            StripePriceId = string.IsNullOrWhiteSpace(stripePriceId) ? null : stripePriceId.Trim(),
            OfferName = string.IsNullOrWhiteSpace(offerName) ? null : offerName.Trim(),
            DailyRate = dailyRate,
            DailyBudget = dailyBudget,
            TrialSpendCap = trialSpendCap,
            CurrencyCode = currencyCode,
            CreatedBy = string.IsNullOrWhiteSpace(actor) ? DefaultActor : actor,
            CreatedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        return new(true, "New package version saved.");
    }
}
