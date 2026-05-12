using System.Text.Json;
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
        if (newNumber.Length > 128)
        {
            throw new BillStateConflictException("Invoice number is longer than 128 characters.");
        }
        if (newNumber.Any(char.IsWhiteSpace))
        {
            throw new BillStateConflictException("Invoice number cannot contain whitespace.");
        }

        var bill = await LoadTrackedBillAsync(billId, cancellationToken);
        if (bill.State is IBillService.StatePosting)
        {
            throw new BillStateConflictException(
                $"Bill '{billId}' is being posted to Tally right now; wait for the current job to settle before changing its number.");
        }

        if (!BillNumberChangeRules.IsDigitsOnly(newNumber)
            || !long.TryParse(newNumber, out var autoFormatCore)
            || autoFormatCore <= 0)
        {
            throw new BillStateConflictException("Invoice number must be positive digits only.");
        }

        // Auto-format digit-only input so admin doesn't have to retype the
        // prefix / fiscal year / suffix boilerplate.
        {
            var (prefix, suffix, padding) = await numberingService.GetEffectivePrefixSuffixAsync(cancellationToken);
            var fiscalYearForFormat = bill.FiscalYear ?? NumberingService.NormalizeFiscalYear(null);
            newNumber = InvoiceNumberFormatter.Format(prefix, suffix, autoFormatCore, fiscalYearForFormat, padding);
        }

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
                     && x.InvoiceNumber == newNumber
                     && x.Id != bill.Id,
                cancellationToken);
        if (conflict)
        {
            throw new BillStateConflictException(
                $"Invoice number '{newNumber}' is already used by another bill in fiscal year '{bill.FiscalYear ?? "(none)"}'.");
        }

        var showroomId = bill.ShowroomId;
        var fiscalYear = bill.FiscalYear ?? NumberingService.NormalizeFiscalYear(null);
        var sequence = await dbContext.InvoiceSequences
            .AsNoTracking()
            .FirstOrDefaultAsync(
                s => s.ShowroomId == showroomId
                     && s.FiscalYear == fiscalYear
                     && s.DocumentType == INumberingService.DocumentTypeSalesInvoice,
                cancellationToken);
        long? seqNext = sequence?.NextValue;

        bool leavesGap = false;
        var parsedCore = BillNumberChangeRules.ExtractTrailingDigits(newNumber);
        if (parsedCore is { } core && seqNext is { } next && core > next - 1)
        {
            leavesGap = true;
        }

        bool tallyDiverges = bill.State is IBillService.StatePosted
            or IBillService.StateFailed;

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
        bill.InvoiceNumber = newNumber;
        bill.UpdatedAtUtc = now;

        auditStore.Write(
            bill.Id,
            "bill.number.changed",
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

        // Save + sequence-rollback share one transaction so a moved-down number
        // that vacates the trailing core (e.g. 94 -> 92 when 94 was the most
        // recent reservation) immediately reclaims the freed slot for the next
        // ReserveAsync. Without the rollback, NextValue would stay anchored past
        // the now-empty core and the next bill would skip 94 forever.
        await using var transaction = UsesInMemoryProvider()
            ? null
            : await dbContext.Database.BeginTransactionAsync(cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);

        if (bill.FiscalYear is not null)
        {
            await RollbackTrailingSequenceAsync(showroomId, bill.FiscalYear, bill.Id, cancellationToken);
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

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

    internal async Task<BillResponse> MarkPostedAsync(
        Guid billId,
        MarkBillStateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var bill = await LoadTrackedBillAsync(billId, cancellationToken);
        if (bill.State is not (IBillService.StatePending or BillStates.Draft or IBillService.StateFailed))
        {
            throw new BillStateConflictException(
                $"Bill '{billId}' is in state '{bill.State}'; mark-as-pushed is only allowed from pending/draft/failed.");
        }

        var now = DateTimeOffset.UtcNow;
        var fromState = bill.State;
        bill.State = IBillService.StatePosted;
        bill.EditedAfterPush = false;
        bill.UpdatedAtUtc = now;

        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        auditStore.Write(
            bill.Id,
            "bill.mark_posted",
            bill.State,
            now,
            new { fromState, reason });
        await dbContext.SaveChangesAsync(cancellationToken);

        var revision = bill.CurrentRevisionId is Guid rid
            ? await dbContext.BillRevisions.AsNoTracking().FirstOrDefaultAsync(r => r.Id == rid, cancellationToken)
            : null;
        return BillSerialization.MapResponse(bill, revision);
    }

    internal async Task<BillResponse> MarkPendingAsync(
        Guid billId,
        MarkBillStateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var bill = await LoadTrackedBillAsync(billId, cancellationToken);
        if (bill.State is not (IBillService.StatePosted or IBillService.StateFailed))
        {
            throw new BillStateConflictException(
                $"Bill '{billId}' is in state '{bill.State}'; mark-as-pending is only allowed from posted/failed.");
        }

        var now = DateTimeOffset.UtcNow;
        var fromState = bill.State;
        bill.State = IBillService.StatePending;
        bill.EditedAfterPush = false;
        bill.UpdatedAtUtc = now;

        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        auditStore.Write(
            bill.Id,
            "bill.mark_pending",
            bill.State,
            now,
            new { fromState, reason });
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
        var bill = await LoadTrackedBillAsync(billId, cancellationToken);
        if (bill.State is IBillService.StatePosting)
        {
            throw new BillStateConflictException(
                $"Bill '{billId}' is being posted to Tally right now; wait for the current job to settle before deleting.");
        }

        bool tallyDiverges = bill.State is IBillService.StatePosted
            or IBillService.StateFailed;

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

        var revision = bill.CurrentRevisionId is Guid rid
            ? await dbContext.BillRevisions.AsNoTracking().FirstOrDefaultAsync(r => r.Id == rid, cancellationToken)
            : null;
        auditStore.Write(
            bill.Id,
            "bill.deleted",
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
        await using var transaction = UsesInMemoryProvider()
            ? null
            : await dbContext.Database.BeginTransactionAsync(cancellationToken);

        // Clear superseded-by back-references
        await ClearSupersededByReferencesAsync(billId, now, cancellationToken);

        // Revisions cascade-delete with the bill. Posting is synchronous and leaves no queue rows behind.
        dbContext.Bills.Remove(bill);
        await dbContext.SaveChangesAsync(cancellationToken);

        if (fiscalYear is not null)
        {
            await RollbackTrailingSequenceAsync(showroomId, fiscalYear, billId, cancellationToken);
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

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
            catch (BillStateConflictException ex)
            {
                items.Add(new DeleteBillItemResult(id, false, false, ex.Message));
                skipped++;
                dbContext.ChangeTracker.Clear();
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

    private async Task<BillEntity> LoadTrackedBillAsync(Guid billId, CancellationToken cancellationToken)
    {
        var bill = await dbContext.Bills.FirstOrDefaultAsync(x => x.Id == billId, cancellationToken);
        if (bill is null)
        {
            throw new BillNotFoundException(billId);
        }
        return bill;
    }

    private async Task ClearSupersededByReferencesAsync(
        Guid billId,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        var query = dbContext.Bills.Where(b => b.SupersededByBillId == billId);
        if (UsesInMemoryProvider())
        {
            var rows = await query.ToListAsync(cancellationToken);
            foreach (var row in rows)
            {
                row.SupersededByBillId = null;
                row.UpdatedAtUtc = updatedAt;
            }

            return;
        }

        await query.ExecuteUpdateAsync(
            setters => setters
                .SetProperty(b => b.SupersededByBillId, (Guid?)null)
                .SetProperty(b => b.UpdatedAtUtc, updatedAt),
            cancellationToken);
    }

    private bool UsesInMemoryProvider() =>
        string.Equals(dbContext.Database.ProviderName, "Microsoft.EntityFrameworkCore.InMemory", StringComparison.Ordinal);

    /// <summary>
    /// After a delete, set <c>InvoiceSequences.NextValue</c> to
    /// <c>min(currentNextValue, max(parsed-trailing-digits across remaining bills in scope) + 1)</c>.
    /// Reclaims trailing freed cores so the next reservation reuses them.
    /// Gaps in the middle are still preserved (deleting bill 25 while bill 49
    /// exists keeps NextValue anchored at 50, not 26).
    ///
    /// Why parse the trailing digits instead of comparing formatted strings:
    /// historical bills can carry an InvoiceNumber whose format differs from
    /// what <see cref="InvoiceNumberFormatter"/> currently produces (e.g.
    /// non-zero-padded "/49" coexisting with new "/0049"). A formatted-EXISTS
    /// check would miss those bills and walk NextValue past genuinely
    /// occupied cores. Parsing trailing digits of <c>InvoiceNumber</c>
    /// equates "/49" and "/0049" to the same core (49), so the sequence
    /// stays consistent across format changes.
    ///
    /// Locks the sequence row (FOR UPDATE on Postgres) so the rollback is
    /// atomic against a concurrent reservation. Bills with no trailing
    /// digits (legacy custom strings) are ignored — they don't anchor the
    /// sequence.
    /// </summary>
    private async Task RollbackTrailingSequenceAsync(
        Guid showroomId,
        string fiscalYear,
        Guid deletedBillId,
        CancellationToken cancellationToken)
    {
        const string documentType = INumberingService.DocumentTypeSalesInvoice;

        InvoiceSequenceEntity? sequence;
        if (UsesInMemoryProvider())
        {
            sequence = await dbContext.InvoiceSequences.FirstOrDefaultAsync(
                x => x.ShowroomId == showroomId
                     && x.FiscalYear == fiscalYear
                     && x.DocumentType == documentType,
                cancellationToken);
        }
        else
        {
            var locked = await dbContext.InvoiceSequences
                .FromSqlInterpolated($@"
SELECT * FROM public.invoice_sequences
WHERE ""ShowroomId"" = {showroomId}
  AND ""FiscalYear"" = {fiscalYear}
  AND ""DocumentType"" = {documentType}
FOR UPDATE")
                .ToListAsync(cancellationToken);
            sequence = locked.FirstOrDefault();
        }

        if (sequence is null || sequence.NextValue <= 1L)
        {
            return;
        }

        var remainingNumbers = await dbContext.Bills
            .AsNoTracking()
            .Where(b => b.ShowroomId == showroomId
                        && b.FiscalYear == fiscalYear
                        && b.InvoiceNumber != null)
            .Select(b => b.InvoiceNumber!)
            .ToListAsync(cancellationToken);

        long maxOccupiedCore = 0L;
        foreach (var number in remainingNumbers)
        {
            if (BillNumberChangeRules.ExtractTrailingDigits(number) is { } core && core > maxOccupiedCore)
            {
                maxOccupiedCore = core;
            }
        }

        var startedAt = sequence.NextValue;
        var target = Math.Max(1L, maxOccupiedCore + 1L);
        if (target >= startedAt)
        {
            return;
        }

        sequence.NextValue = target;
        sequence.UpdatedAtUtc = DateTimeOffset.UtcNow;

        dbContext.AuditEvents.Add(new AuditEventEntity
        {
            Id = Guid.NewGuid(),
            EntityType = "numbering",
            EntityId = $"{showroomId}|{fiscalYear}|{documentType}",
            EventType = $"numbering.{documentType}.rolled_back",
            ActorType = "system",
            PayloadJson = JsonSerializer.Serialize(new
            {
                fiscalYear,
                from = startedAt,
                to = sequence.NextValue,
                maxOccupiedCore,
                triggeredByBillId = deletedBillId
            }),
            CreatedAtUtc = sequence.UpdatedAtUtc
        });

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
