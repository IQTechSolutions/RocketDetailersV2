using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RD.Domain.Entities;

namespace RD.Infrastructure;

/// <summary>
/// Depth-1 of append-only enforcement (depth-2 is restrictive FK delete
/// behavior; depth-3 is DB-level permissions on the production login).
/// Modifying or deleting a ledger/decision/audit row throws — corrections
/// are compensating entries.
/// </summary>
public sealed class AppendOnlyInterceptor : SaveChangesInterceptor
{
    private static readonly Type[] AppendOnlyTypes =
    [
        typeof(LedgerEntry), typeof(Decision),
        // Evidence rows are never deleted, but their verification trail
        // (TriggeredAt/VerifiedAt/FailureReason) is filled in over the row's
        // life — delete-protected, not update-protected, like WebhookInboxItem.
        typeof(DunningAttempt),
        typeof(WebhookInboxItem) // status fields are the ONLY mutable columns — see exception below
    ];

    private static readonly Type[] StrictlyImmutable =
    [
        typeof(LedgerEntry), typeof(Decision)
    ];

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Enforce(eventData.Context!);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Enforce(eventData.Context!);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static void Enforce(DbContext context)
    {
        foreach (var entry in context.ChangeTracker.Entries())
        {
            var type = entry.Entity.GetType();
            if (entry.State == EntityState.Deleted && AppendOnlyTypes.Contains(type))
                throw new InvalidOperationException($"{type.Name} is append-only: deletes are forbidden. Write a compensating entry.");
            if (entry.State == EntityState.Modified && StrictlyImmutable.Contains(type))
                throw new InvalidOperationException($"{type.Name} is immutable: updates are forbidden. Write a compensating entry.");
        }
    }
}
