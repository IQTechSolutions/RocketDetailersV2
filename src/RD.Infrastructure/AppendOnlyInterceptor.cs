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
        // DunningAttempt and WebhookInboxItem are append-only for DELETES, but their
        // lifecycle columns are meant to be filled in after insert:
        //   DunningAttempt — TriggeredAt (dispatcher) then VerifiedAt/FailureReason
        //                    (the delayed GHL delivery-verification job).
        //   WebhookInboxItem — status fields.
        //   MetaShadowPrediction — EndedAt when the exact recommendation stops.
        // So they are NOT in StrictlyImmutable below.
        typeof(DunningAttempt), typeof(WebhookInboxItem),
        typeof(MetaShadowPrediction),
        typeof(MetaActivityFact),
        typeof(StripeCustomerPreferenceChange)
    ];

    private static readonly Type[] StrictlyImmutable =
    [
        typeof(LedgerEntry), typeof(Decision), typeof(MetaActivityFact),
        typeof(StripeCustomerPreferenceChange)
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
