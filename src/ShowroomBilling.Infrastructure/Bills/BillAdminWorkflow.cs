using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Application.Bills;
using ShowroomBilling.Application.Numbering;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Numbering;
using ShowroomBilling.Infrastructure.Numbering;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Infrastructure.Persistence.Entities;

namespace ShowroomBilling.Infrastructure.Bills;

internal sealed class BillAdminWorkflow(
    ShowroomBillingDbContext dbContext,
    INumberingService numberingService,
    BillAuditStore auditStore)
{
    internal async Task<ChangeBillNumberResponse> ChangeInvoiceNumberAsync(
        Guid billId,
        ChangeBillNumberRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var raw = request.NewInvoiceNumber ?? string.Empty;
        var newNumber = raw.Trim();
        if (string.IsNullOrWhiteSpace(newNumber))
        {
            throw new BillStateConflictException("Invoice number cannot be blank.");
        }

        var bill = await dbContext.LoadTrackedBillAsync(billId, cancellationToken);
        if (bill.State is BillStates.Posting or BillStates.ReconciliationRequired)
        {
            throw new BillStateConflictException(
                $"Bill '{billId}' cannot change number while its Tally outcome is unresolved.");
        }

        if (!BillNumberChangeRules.IsDigitsOnly(newNumber)
            || !long.TryParse(newNumber, out var autoFormatCore)
            || autoFormatCore <= 0)
        {
            throw new BillStateConflictException("Invoice number must be positive digits only.");
        }

        // Auto-format digit-only input so admin doesn't have to retype the
        // prefix / fiscal year / suffix boilerplate.
        var fiscalYear = bill.FiscalYear ?? NumberingService.NormalizeFiscalYear(null);
        var (prefix, suffix, padding) = await numberingService.GetEffectivePrefixSuffixAsync(cancellationToken);
        newNumber = InvoiceNumberFormatter.Format(prefix, suffix, autoFormatCore, fiscalYear, padding);

        if (string.Equals(bill.InvoiceNumber, newNumber, StringComparison.Ordinal))
        {
            return new ChangeBillNumberResponse(
                BillId: bill.Id,
                OldInvoiceNumber: bill.InvoiceNumber,
                NewInvoiceNumber: newNumber,
                Committed: false,
                LeavesGap: false,
                TallyDiverges: false,
                ReservationOrphaned: false,
                SequenceNextValue: null,
                WarningSummary: "New invoice number matches the current one; nothing to do.");
        }

        var conflict = await dbContext.Bills
            .AsNoTracking()
            .AnyAsync(
                x => x.ShowroomId == bill.ShowroomId
                     && x.FiscalYear == bill.FiscalYear
                     && (x.InvoiceNumber == newNumber || x.InvoiceCore == autoFormatCore)
                     && x.Id != bill.Id,
                cancellationToken);
        if (conflict)
        {
            throw new BillStateConflictException(
                $"Invoice number '{newNumber}' is already used by another bill in fiscal year '{bill.FiscalYear ?? "(none)"}'.");
        }

        var showroomId = bill.ShowroomId;
        var sequence = await dbContext.InvoiceSequences
            .AsNoTracking()
            .FirstOrDefaultAsync(
                s => s.ShowroomId == showroomId
                     && s.FiscalYear == fiscalYear
                     && s.DocumentType == INumberingService.DocumentTypeSalesInvoice,
                cancellationToken);
        long? seqNext = sequence?.NextValue;

        bool leavesGap = seqNext is { } next && autoFormatCore > next - 1;

        // Tally holds the voucher under the old number. Committing the rename
        // reopens the bill as an edit-after-push so the next push Alters that
        // voucher to the new number instead of creating a second one.
        bool tallyDiverges = await BillTallyHistory.IsInTallyAsync(dbContext, bill, cancellationToken);

        bool reservationOrphaned = false;
        if (!string.IsNullOrWhiteSpace(bill.InvoiceNumber))
        {
            reservationOrphaned = await dbContext.InvoiceNumberReservations
                .AsNoTracking()
                .AnyAsync(
                    r => r.IdempotencyKey == $"draft:{bill.Id:N}"
                         && r.FormattedNumber != newNumber,
                    cancellationToken);
        }

        if (request.DryRun)
        {
            return new ChangeBillNumberResponse(
                BillId: bill.Id,
                OldInvoiceNumber: bill.InvoiceNumber,
                NewInvoiceNumber: newNumber,
                Committed: false,
                LeavesGap: leavesGap,
                TallyDiverges: tallyDiverges,
                ReservationOrphaned: reservationOrphaned,
                SequenceNextValue: seqNext,
                WarningSummary: BillNumberChangeRules.BuildWarning(leavesGap, tallyDiverges, reservationOrphaned));
        }

        var now = DateTimeOffset.UtcNow;
        var oldNumber = bill.InvoiceNumber;
        var oldCore = bill.InvoiceCore ?? InvoiceNumberFormatter.TryParseTrailingCore(oldNumber);
        var fromState = bill.State;
        bill.InvoiceNumber = newNumber;
        bill.InvoiceCore = autoFormatCore;
        bill.UpdatedAtUtc = now;
        if (tallyDiverges)
        {
            bill.EditedAfterPush = true;
            if (bill.State == BillStates.Posted)
            {
                bill.State = BillStates.Pending;
            }
        }

        auditStore.Write(
            bill.Id,
            BillAuditEvents.NumberChanged,
            bill.State,
            now,
            new
            {
                old = oldNumber,
                @new = newNumber,
                leavesGap,
                tallyDiverges,
                reservationOrphaned,
                reason = request.Reason ?? string.Empty
            });
        if (tallyDiverges)
        {
            auditStore.Write(
                bill.Id,
                BillAuditEvents.EditReopened,
                bill.State,
                now,
                new
                {
                    fromState,
                    invoiceNumber = newNumber,
                    previousInvoiceNumber = oldNumber,
                    reason = "invoice-number-changed"
                });
        }

        // Save + sequence-rollback share one transaction so a moved-down number
        // that vacates the trailing core (e.g. 94 -> 92 when 94 was the most
        // recent reservation) immediately reclaims the freed slot for the next
        // ReserveAsync. Without the rollback, NextValue would stay anchored past
        // the now-empty core and the next bill would skip 94 forever.
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);

        // Tally still holds the old number until the Alter lands, so its core
        // stays reserved instead of going to the next new bill.
        if (bill.FiscalYear is not null)
        {
            await NumberingSequenceReclaimer.ReclaimAsync(
                dbContext,
                showroomId,
                bill.FiscalYear,
                $"rename:{bill.Id:N}",
                cancellationToken,
                keepCoreReserved: tallyDiverges ? oldCore : null);
        }

        await transaction.CommitAsync(cancellationToken);

        // Re-read the sequence so the response reflects the post-rollback value
        // when the rename freed the trailing core.
        var postSeqNext = await dbContext.InvoiceSequences
            .AsNoTracking()
            .Where(s => s.ShowroomId == showroomId
                        && s.FiscalYear == fiscalYear
                        && s.DocumentType == INumberingService.DocumentTypeSalesInvoice)
            .Select(s => (long?)s.NextValue)
            .FirstOrDefaultAsync(cancellationToken);

        return new ChangeBillNumberResponse(
            BillId: bill.Id,
            OldInvoiceNumber: oldNumber,
            NewInvoiceNumber: newNumber,
            Committed: true,
            LeavesGap: leavesGap,
            TallyDiverges: tallyDiverges,
            ReservationOrphaned: reservationOrphaned,
            SequenceNextValue: postSeqNext ?? seqNext,
            WarningSummary: BillNumberChangeRules.BuildWarning(leavesGap, tallyDiverges, reservationOrphaned));
    }

    internal Task<BillResponse> MarkPostedAsync(
        Guid billId,
        MarkBillStateRequest request,
        CancellationToken cancellationToken = default)
        => MarkAsync(
            billId,
            request,
            BillStateCapabilities.CanMarkPosted,
            BillStates.Posted,
            BillAuditEvents.MarkPosted,
            "mark-as-pushed",
            cancellationToken);

    internal Task<BillResponse> MarkPendingAsync(
        Guid billId,
        MarkBillStateRequest request,
        CancellationToken cancellationToken = default)
        => MarkAsync(
            billId,
            request,
            BillStateCapabilities.CanMarkPending,
            BillStates.Pending,
            BillAuditEvents.MarkPending,
            "mark-as-pending",
            cancellationToken);

    /// <summary>
    /// The admin override that settles a bill's state without calling Tally:
    /// the operator has checked Tally and says what it holds.
    /// </summary>
    private async Task<BillResponse> MarkAsync(
        Guid billId,
        MarkBillStateRequest request,
        Func<string?, bool> isAllowedFrom,
        string targetState,
        string eventType,
        string actionName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var bill = await dbContext.LoadTrackedBillAsync(billId, cancellationToken);
        if (!isAllowedFrom(bill.State))
        {
            throw new BillStateConflictException(
                $"Bill '{billId}' is in state '{bill.State}'; {actionName} is not allowed.");
        }

        var now = DateTimeOffset.UtcNow;
        var fromState = bill.State;
        bill.State = targetState;
        bill.EditedAfterPush = false;
        bill.UpdatedAtUtc = now;

        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        auditStore.Write(bill.Id, eventType, bill.State, now, new { fromState, reason });
        await dbContext.SaveChangesAsync(cancellationToken);

        var revision = bill.CurrentRevisionId is Guid rid
            ? await dbContext.BillRevisions.AsNoTracking().FirstOrDefaultAsync(r => r.Id == rid, cancellationToken)
            : null;
        return BillSerialization.MapResponse(bill, revision);
    }

    internal async Task<DeleteBillResponse> DeleteAsync(
        Guid billId,
        DeleteBillRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var bill = await dbContext.LoadTrackedBillAsync(billId, cancellationToken);
        if (bill.State is BillStates.Posting or BillStates.ReconciliationRequired)
        {
            throw new BillStateConflictException(
                $"Bill '{billId}' cannot be deleted while its Tally outcome is unresolved.");
        }

        var inTally = await BillTallyHistory.IsInTallyAsync(dbContext, bill, cancellationToken);
        bool tallyDiverges = inTally || bill.State is BillStates.Failed;

        if (request.DryRun)
        {
            return new DeleteBillResponse(
                BillId: bill.Id,
                Committed: false,
                TallyDiverges: tallyDiverges,
                LastState: bill.State,
                InvoiceNumber: bill.InvoiceNumber);
        }

        var now = DateTimeOffset.UtcNow;
        var lastState = bill.State;
        var invoiceNumber = bill.InvoiceNumber;
        var showroomId = bill.ShowroomId;
        var fiscalYear = bill.FiscalYear;
        var core = bill.InvoiceCore ?? InvoiceNumberFormatter.TryParseTrailingCore(invoiceNumber);

        var revision = bill.CurrentRevisionId is Guid rid
            ? await dbContext.BillRevisions.AsNoTracking().FirstOrDefaultAsync(r => r.Id == rid, cancellationToken)
            : null;
        auditStore.Write(
            bill.Id,
            BillAuditEvents.Deleted,
            lastState,
            now,
            new
            {
                lastState,
                invoiceNumber = invoiceNumber ?? string.Empty,
                partyName = revision?.PartyName ?? string.Empty,
                grandTotal = revision?.GrandTotal ?? 0m,
                tallyDiverges,
                reason = request.Reason ?? string.Empty
            });

        // Backref-clear + delete + sequence-rollback share one transaction so
        // a failed delete doesn't leave dangling cleared references on prior
        // bills, and a failed rollback doesn't leak an inconsistent NextValue.
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        // Clear superseded-by back-references
        await ClearSupersededByReferencesAsync(billId, now, cancellationToken);

        // Revisions cascade-delete with the bill. Posting is synchronous and leaves no queue rows behind.
        dbContext.Bills.Remove(bill);
        await dbContext.SaveChangesAsync(cancellationToken);

        // A deleted bill whose voucher Tally still holds keeps its number in
        // Tally, so its core stays reserved for good.
        if (fiscalYear is not null)
        {
            await NumberingSequenceReclaimer.ReclaimAsync(
                dbContext,
                showroomId,
                fiscalYear,
                $"delete:{billId:N}",
                cancellationToken,
                keepCoreReserved: inTally ? core : null);
        }

        await transaction.CommitAsync(cancellationToken);

        return new DeleteBillResponse(
            BillId: billId,
            Committed: true,
            TallyDiverges: tallyDiverges,
            LastState: lastState,
            InvoiceNumber: invoiceNumber);
    }

    internal async Task<DeleteSelectedBillsResponse> DeleteSelectedAsync(
        DeleteSelectedBillsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ordered = BillAdminSelection.NormalizeOrderedIds(request.BillIds);
        if (ordered.Count == 0)
        {
            return new DeleteSelectedBillsResponse(0, 0, 0, Array.Empty<DeleteBillItemResult>());
        }

        var items = new List<DeleteBillItemResult>(ordered.Count);
        var deleted = 0;
        var skipped = 0;
        foreach (var id in ordered)
        {
            try
            {
                var single = await DeleteAsync(id, new DeleteBillRequest(request.Reason, DryRun: false), cancellationToken);
                items.Add(new DeleteBillItemResult(id, true, single.TallyDiverges, null));
                deleted++;
            }
            catch (BillNotFoundException)
            {
                items.Add(new DeleteBillItemResult(id, false, false, "Bill not found."));
                skipped++;
            }
            catch (Exception ex)
            {
                items.Add(new DeleteBillItemResult(id, false, false, ex.Message));
                skipped++;
                dbContext.ChangeTracker.Clear();
            }
        }

        return new DeleteSelectedBillsResponse(ordered.Count, deleted, skipped, items);
    }


    private async Task ClearSupersededByReferencesAsync(
        Guid billId,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        await dbContext.Bills
            .Where(b => b.SupersededByBillId == billId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(b => b.SupersededByBillId, (Guid?)null)
                    .SetProperty(b => b.UpdatedAtUtc, updatedAt),
                cancellationToken);
    }
}
