using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Infrastructure.Persistence.Entities;

namespace ShowroomBilling.Infrastructure.Bills;

/// <summary>
/// Answers "does Tally hold a voucher for this bill right now?" from the bill
/// row plus its append-only audit trail. A bill is in Tally when it is posted,
/// when it was edited after a successful push (<c>EditedAfterPush</c>), or when
/// the most recent Tally-truth event is a successful push / admin Mark as
/// Pushed. An admin Mark as Pending after those events means the operator
/// confirmed Tally does not hold it.
/// </summary>
internal static class BillTallyHistory
{
    private const string TallyPosted = "tally.posted";
    private const string MarkPosted = "bill.mark_posted";
    private const string MarkPending = "bill.mark_pending";

    internal static async Task<bool> IsInTallyAsync(
        ShowroomBillingDbContext dbContext,
        BillEntity bill,
        CancellationToken cancellationToken)
    {
        if (bill.State == BillStates.Posted || bill.EditedAfterPush)
        {
            return true;
        }

        var billIdText = bill.Id.ToString();
        var lastTruthEvent = await dbContext.AuditEvents.AsNoTracking()
            .Where(e => e.EntityType == "bill"
                && e.EntityId == billIdText
                && (e.EventType == TallyPosted || e.EventType == MarkPosted || e.EventType == MarkPending))
            .OrderByDescending(e => e.CreatedAtUtc)
            .ThenByDescending(e => e.Id)
            .Select(e => e.EventType)
            .FirstOrDefaultAsync(cancellationToken);

        return lastTruthEvent is TallyPosted or MarkPosted;
    }
}
