using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Infrastructure.Persistence.Entities;
using ShowroomBilling.Infrastructure.Tally;

namespace ShowroomBilling.Infrastructure.Bills;

/// <summary>
/// Reads the bill and its audit trail to determine whether Tally holds its voucher
/// and which voucher an edited bill should alter.
/// </summary>
internal static class BillTallyHistory
{
    private const string TallyPosted = BillAuditEvents.TallyPosted;
    private const string MarkPosted = BillAuditEvents.MarkPosted;
    private const string MarkPending = BillAuditEvents.MarkPending;

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

    internal sealed record AlterHistory(
        string? MasterId, string? Company, string VoucherNumber, DateOnly? BillDate, decimal? GrandTotal);

    /// <summary>Reads the voucher identity, number and revision Tally last held in one database snapshot.</summary>
    internal static async Task<AlterHistory> ReadForAlterAsync(
        ShowroomBillingDbContext dbContext, BillEntity bill, CancellationToken cancellationToken)
    {
        var id = bill.Id.ToString();
        var trail = dbContext.AuditEvents.AsNoTracking().Where(e => e.EntityType == "bill" && e.EntityId == id);

        // An id comes from a successful push strictly before the latest reopening.
        // Mark as Pending invalidates every push up to and including its timestamp.
        var recordedPush = trail
            .Where(push => push.EventType == TallyPosted
                && trail.Any(reopen => reopen.EventType == BillAuditEvents.EditReopened && reopen.CreatedAtUtc > push.CreatedAtUtc)
                && !trail.Any(reset => reset.EventType == MarkPending && reset.CreatedAtUtc >= push.CreatedAtUtc))
            .OrderByDescending(push => push.CreatedAtUtc).ThenByDescending(push => push.Id)
            .Select(push => push.PayloadJson);

        // A hand-mark also establishes the date and total to check. A later rename only
        // takes effect in Tally when the Alter succeeds, so take the first such rename.
        var held = await trail
            .Where(e => e.EventType == TallyPosted || e.EventType == MarkPosted)
            .OrderByDescending(e => e.CreatedAtUtc).ThenByDescending(e => e.Id)
            .Select(last => new
            {
                PostedPayload = recordedPush.FirstOrDefault(),
                RenamePayload = trail
                    .Where(e => e.EventType == BillAuditEvents.NumberChanged && e.CreatedAtUtc > last.CreatedAtUtc)
                    .OrderBy(e => e.CreatedAtUtc).ThenBy(e => e.Id)
                    .Select(e => e.PayloadJson)
                    .FirstOrDefault(),
                Revision = dbContext.BillRevisions
                    .Where(r => r.BillId == bill.Id && r.CreatedAtUtc <= last.CreatedAtUtc)
                    .OrderByDescending(r => r.CreatedAtUtc).ThenByDescending(r => r.RevisionNo)
                    .Select(r => new { r.BillDate, r.GrandTotal })
                    .FirstOrDefault()
            })
            .FirstOrDefaultAsync(cancellationToken);

        var (masterId, company) = RecordedVoucher(held?.PostedPayload);
        return new AlterHistory(masterId, company, NumberBeforeRename(bill.InvoiceNumber!, held?.RenamePayload),
            held?.Revision?.BillDate, held?.Revision?.GrandTotal);
    }

    internal static (string? MasterId, string? Company) RecordedVoucher(string? postedPayload)
    {
        // Only old pushes without a tallyMasterId field used remoteId as the voucher id.
        // A present but null id means Tally supplied none, even if remoteId looks numeric.
        var masterId = BillAuditStore.ReadDetailsString(postedPayload, "tallyMasterId", legacyProperty: "remoteId");
        var company = BillAuditStore.ReadDetailsString(postedPayload, "company");
        return (TallyIds.NormalizePositiveInteger(masterId), string.IsNullOrWhiteSpace(company) ? null : company.Trim());
    }

    internal static string NumberBeforeRename(string current, string? renamePayload)
    {
        var before = BillAuditStore.ReadDetailsString(renamePayload, "old");
        return string.IsNullOrWhiteSpace(before) ? current : before;
    }
}
