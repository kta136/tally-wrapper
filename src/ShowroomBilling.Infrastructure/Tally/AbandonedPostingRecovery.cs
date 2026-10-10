using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Infrastructure.Persistence.Entities;

namespace ShowroomBilling.Infrastructure.Tally;

/// <summary>
/// Moves push attempts that can no longer be alive from <c>posting</c> to
/// <c>reconciliation_required</c>. Several counters' APIs can share one
/// database, so a bill in <c>posting</c> may be a live push on another
/// counter. A push cannot outlast its own budgets (the Tally call is capped
/// at 300 s and settling at 30 s), so only claims older than
/// <see cref="AbandonAfter"/> are treated as abandoned. Each transition is
/// conditional on the bill still holding the same claim, so it never
/// overwrites an outcome that settled in the meantime. Local state repair
/// only; this never contacts Tally.
/// </summary>
public static class AbandonedPostingRecovery
{
    public static readonly TimeSpan AbandonAfter = TimeSpan.FromMinutes(10);


    /// <returns>
    /// How many bills were recovered, and when the youngest remaining
    /// <c>posting</c> claim becomes old enough to check again (null if none).
    /// </returns>
    public static async Task<(int Recovered, DateTimeOffset? NextCheckAtUtc)> RecoverAsync(
        ShowroomBillingDbContext db,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var cutoff = nowUtc - AbandonAfter;
        var claims = await db.Bills
            .AsNoTracking()
            .Where(b => b.State == BillStates.Posting)
            .Select(b => new { b.Id, b.UpdatedAtUtc })
            .ToListAsync(cancellationToken);

        var recovered = 0;
        DateTimeOffset? nextCheck = null;
        foreach (var claim in claims)
        {
            if (claim.UpdatedAtUtc > cutoff)
            {
                var due = claim.UpdatedAtUtc + AbandonAfter;
                nextCheck = nextCheck is null || due < nextCheck ? due : nextCheck;
                continue;
            }

            if (await TryMoveToReconciliationAsync(db, claim.Id, claim.UpdatedAtUtc, nowUtc, cancellationToken))
            {
                recovered++;
            }
        }

        return (recovered, nextCheck);
    }

    private static async Task<bool> TryMoveToReconciliationAsync(
        ShowroomBillingDbContext db,
        Guid billId,
        DateTimeOffset claimedAtUtc,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        int affected;
        if (db.UsesInMemoryProvider())
        {
            var bill = await db.Bills.FirstOrDefaultAsync(b => b.Id == billId, cancellationToken);
            affected = bill is { State: BillStates.Posting } && bill.UpdatedAtUtc == claimedAtUtc ? 1 : 0;
            if (affected == 1)
            {
                bill!.State = BillStates.ReconciliationRequired;
                bill.UpdatedAtUtc = nowUtc;
            }
        }
        else
        {
            affected = await db.Bills
                .Where(b => b.Id == billId
                    && b.State == BillStates.Posting
                    && b.UpdatedAtUtc == claimedAtUtc)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.State, BillStates.ReconciliationRequired)
                    .SetProperty(b => b.UpdatedAtUtc, nowUtc),
                    cancellationToken);
        }

        if (affected == 0)
        {
            return false;
        }

        db.AuditEvents.Add(new AuditEventEntity
        {
            Id = Guid.CreateVersion7(),
            EntityType = "bill",
            EntityId = billId.ToString(),
            EventType = "bill.posting.recovered",
            ActorType = "system",
            PayloadJson = "{\"reason\":\"A push was left in posting longer than any push can run (API stopped mid-post); Tally may have accepted the voucher. Reconciliation is required before another write.\"}",
            CreatedAtUtc = nowUtc,
        });
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
