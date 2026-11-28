using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Application.Admin;
using ShowroomBilling.Application.Bills;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Numbering;
using ShowroomBilling.Infrastructure.Numbering;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Infrastructure.Persistence.Entities;

namespace ShowroomBilling.Infrastructure.Bills;

public sealed class BillLifecycleWorkflow(
    ShowroomBillingDbContext dbContext,
    NumberingService numberingService,
    BillAuditStore auditStore)
{

    public Task<BillResponse> CreateDraftAsync(
        CreateBillDraftRequest request,
        CancellationToken cancellationToken = default)
        => CreateDraftCoreAsync(request, DateTimeOffset.UtcNow, backdated: false, cancellationToken);

    /// <summary>
    /// Creates a draft whose created and updated times, first revision and
    /// audit event all carry <paramref name="createdAtUtc"/> instead of now.
    /// The synthetic batch uses it to spread bills over a past window. The
    /// invoice number is still the next one in the series.
    /// </summary>
    public Task<BillResponse> CreateBackdatedDraftAsync(
        CreateBillDraftRequest request,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken = default)
        => CreateDraftCoreAsync(request, createdAtUtc, backdated: true, cancellationToken);

    /// <param name="callerIsAdmin">
    /// True when the request came from an unlocked admin session. Saving an
    /// edit to a posted bill reopens it for another push, which only admin may do.
    /// </param>
    public async Task<BillResponse> UpdateDraftAsync(
        Guid billId,
        UpdateBillDraftRequest request,
        bool callerIsAdmin = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Payload);
        BillValidator.Validate(request.Payload);

        var bill = await dbContext.LoadTrackedBillAsync(billId, cancellationToken);
        if (BillStateCapabilities.HasOpenTallyOutcome(bill.State))
        {
            throw new BillStateConflictException(
                $"Bill '{billId}' cannot be edited while its Tally outcome is unresolved.");
        }
        if (!BillStateCapabilities.CanEdit(bill.State))
        {
            throw new BillStateConflictException(
                $"Bill '{billId}' is in state '{bill.State}' and cannot be edited.");
        }
        // Saving an edit to a posted bill sends it back to pending, so the
        // next push changes a voucher Tally already holds.
        if (bill.State is BillStates.Posted && !callerIsAdmin)
        {
            throw new AdminSessionRequiredException(
                $"Bill {bill.InvoiceNumber ?? billId.ToString()} is already posted. Unlock admin to edit it.");
        }
        EnsureBillDateMatchesFiscalYear(bill, request.Payload);

        if (request.ExpectedRevisionId is Guid expectedRevisionId && bill.CurrentRevisionId != expectedRevisionId)
        {
            var current = bill.CurrentRevisionId is Guid currentId
                ? await dbContext.BillRevisions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == currentId, cancellationToken)
                : null;

            // The client's earlier save landed but its response was lost; the
            // retry carries the same payload. Return what is stored.
            if (current is not null
                && current.SupersedesRevisionId == expectedRevisionId
                && BillSerialization.PayloadMatches(current, request.Payload))
            {
                return BillSerialization.MapResponse(bill, current);
            }

            throw new BillStateConflictException(
                $"Bill {bill.InvoiceNumber ?? billId.ToString()} was changed elsewhere after you opened it (now revision {current?.RevisionNo.ToString() ?? "?"}). Reload it from Bills before saving.");
        }

        var reopenFrom = !BillStateCapabilities.IsPending(bill.State)
            ? bill.State
            : null;

        var now = DateTimeOffset.UtcNow;
        var lastRevisionNo = await dbContext.BillRevisions
            .Where(x => x.BillId == billId)
            .Select(x => (int?)x.RevisionNo)
            .MaxAsync(cancellationToken) ?? 0;

        var revision = BillSerialization.BuildRevision(
            billId,
            request.Payload,
            revisionNo: lastRevisionNo + 1,
            supersedes: bill.CurrentRevisionId,
            createdAt: now);

        bill.CurrentRevisionId = revision.Id;
        bill.UpdatedAtUtc = now;

        dbContext.BillRevisions.Add(revision);

        // Only a bill whose voucher Tally actually holds is re-pushed as an
        // Alter. A definite rejection on the first push leaves nothing in
        // Tally, so editing that bill must keep the next push a Create.
        var inTally = reopenFrom is not null
            && await BillTallyHistory.IsInTallyAsync(dbContext, bill, cancellationToken);

        // Reopening a posted or failed bill writes no history line: the new
        // revision is kept, and the next push finds the voucher to alter from
        // the last push (BillTallyHistory.ReadForAlterAsync).
        if (reopenFrom is not null)
        {
            bill.State = BillStates.Pending;
            bill.EditedAfterPush = inTally;
        }
        else
        {
            auditStore.Write(bill.Id, BillAuditEvents.PendingUpdated, bill.State, now);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return BillSerialization.MapResponse(bill, revision);
    }

    public async Task<BillResponse> ReviseAsync(
        Guid billId,
        ReviseBillRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var priorBill = await dbContext.LoadTrackedBillAsync(billId, cancellationToken);
        if (!BillStateCapabilities.CanRevise(priorBill.State))
        {
            throw new BillStateConflictException(
                $"Bill '{billId}' is in state '{priorBill.State}'; revise is currently only supported from 'pending'.");
        }

        var priorRevision = priorBill.CurrentRevisionId is Guid id
            ? await dbContext.BillRevisions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            : null;

        BillPayloadDto payload;
        if (request.InitialPayload is not null)
        {
            BillValidator.Validate(request.InitialPayload);
            payload = request.InitialPayload;
        }
        else if (priorRevision is not null)
        {
            // A replay of what is already stored; it was validated when it was saved.
            payload = BillSerialization.DeserializePayload(priorRevision);
        }
        else
        {
            throw new BillStateConflictException(
                $"Bill '{billId}' has no current revision; cannot derive payload for revise.");
        }

        var now = DateTimeOffset.UtcNow;
        var newBillId = Guid.CreateVersion7();

        // The revised bill is a new bill and needs its own number, exactly
        // like a fresh draft. Reservation, new bill, supersession and audit
        // commit together so a failure leaves the original bill untouched.
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var reservation = await ReserveNumberAsync(payload.BillDate, cancellationToken);

        // A reservation retry clears the change tracker; re-attach the prior bill.
        priorBill = await dbContext.LoadTrackedBillAsync(billId, cancellationToken);

        var newBill = NewPendingBill(newBillId, reservation, now);
        var newRevision = BillSerialization.BuildRevision(newBill.Id, payload, revisionNo: 1, supersedes: priorBill.CurrentRevisionId, createdAt: now);
        newBill.CurrentRevisionId = newRevision.Id;

        priorBill.State = BillStates.Revised;
        priorBill.SupersededByBillId = newBill.Id;
        priorBill.UpdatedAtUtc = now;

        dbContext.Bills.Add(newBill);
        dbContext.BillRevisions.Add(newRevision);
        auditStore.Write(priorBill.Id, BillAuditEvents.Revised, priorBill.State, now, new { supersededByBillId = newBill.Id });
        auditStore.Write(
            newBill.Id,
            BillAuditEvents.PendingCreated,
            newBill.State,
            now,
            new
            {
                supersedesBillId = priorBill.Id,
                invoiceNumber = reservation.FormattedNumber,
                fiscalYear = reservation.FiscalYear
            });
        await dbContext.SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return BillSerialization.MapResponse(newBill, newRevision);
    }

    public async Task<BillResponse> VoidAsync(
        Guid billId,
        VoidBillRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var bill = await dbContext.LoadTrackedBillAsync(billId, cancellationToken);
        if (!BillStateCapabilities.CanVoid(bill.State))
        {
            throw new BillStateConflictException(
                $"Bill '{billId}' is in state '{bill.State}'; void is only allowed from pending or failed in this phase.");
        }

        // Void never contacts Tally. A bill whose voucher Tally still holds
        // (edited after a push, or a failed repost/alter) would end up voided
        // here while the sale and GST stay live in Tally.
        if (await BillTallyHistory.IsInTallyAsync(dbContext, bill, cancellationToken))
        {
            throw new BillStateConflictException(
                $"Bill '{bill.InvoiceNumber ?? billId.ToString()}' is already in Tally. Cancel the voucher in Tally first, then use admin Mark as Pending before voiding it here.");
        }

        var now = DateTimeOffset.UtcNow;
        bill.State = BillStates.Voided;
        bill.UpdatedAtUtc = now;

        var reason = string.IsNullOrWhiteSpace(request.Reason) ? "unspecified" : request.Reason.Trim();
        auditStore.Write(bill.Id, BillAuditEvents.Voided, bill.State, now, new { reason });

        await dbContext.SaveChangesAsync(cancellationToken);

        BillRevisionEntity? currentRevision = null;
        if (bill.CurrentRevisionId is Guid id)
        {
            currentRevision = await dbContext.BillRevisions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        }
        return BillSerialization.MapResponse(bill, currentRevision);
    }

    private async Task<BillResponse> CreateDraftCoreAsync(
        CreateBillDraftRequest request,
        DateTimeOffset now,
        bool backdated,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Payload);
        BillValidator.Validate(request.Payload);

        if (request.ClientOperationId is not Guid operationId)
        {
            return await CreateNewDraftAsync(request, now, backdated, cancellationToken);
        }

        // A retried Save whose first attempt committed but whose response was
        // lost must not create and number a second bill.
        var existing = await FindByOperationIdAsync(operationId, cancellationToken);
        if (existing is not null)
        {
            return await ReplayCreateAsync(existing, request.Payload, cancellationToken);
        }

        try
        {
            return await CreateNewDraftAsync(request, now, backdated, cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Two copies of the same save raced; the other one won.
            dbContext.ChangeTracker.Clear();
            existing = await FindByOperationIdAsync(operationId, cancellationToken);
            if (existing is null)
            {
                throw;
            }
            return await ReplayCreateAsync(existing, request.Payload, cancellationToken);
        }
    }

    private Task<BillEntity?> FindByOperationIdAsync(Guid operationId, CancellationToken cancellationToken) =>
        dbContext.Bills.AsNoTracking().FirstOrDefaultAsync(x => x.CreateOperationId == operationId, cancellationToken);

    private async Task<BillResponse> ReplayCreateAsync(
        BillEntity existing,
        BillPayloadDto payload,
        CancellationToken cancellationToken)
    {
        var firstRevision = await dbContext.BillRevisions.AsNoTracking()
            .FirstOrDefaultAsync(x => x.BillId == existing.Id && x.RevisionNo == 1, cancellationToken);
        if (firstRevision is null || !BillSerialization.PayloadMatches(firstRevision, payload))
        {
            throw new BillStateConflictException(
                $"This save was already recorded as invoice {existing.InvoiceNumber ?? existing.Id.ToString()} with different contents. Open that bill from Bills to change it.");
        }

        var current = existing.CurrentRevisionId is Guid currentId
            ? await dbContext.BillRevisions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == currentId, cancellationToken)
            : firstRevision;
        return BillSerialization.MapResponse(existing, current);
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.GetBaseException() is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation };

    private async Task<BillResponse> CreateNewDraftAsync(
        CreateBillDraftRequest request,
        DateTimeOffset now,
        bool backdated,
        CancellationToken cancellationToken)
    {
        var billId = Guid.CreateVersion7();

        // The number and the bill/revision/audit share one transaction so a
        // failed bill persist doesn't use up an invoice number.
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var reservation = await ReserveNumberAsync(request.Payload.BillDate, cancellationToken);

        // A backdated bill takes the next number, so no bill may come after it, or the two would be
        // numbered in one order and dated in the other: none of a later date, and none of its own
        // date made in its minute or after. A bill of an earlier date never does, whenever it was
        // saved (the rule of SyntheticBatchExecutor.EarliestBillTimeAsync). The year's sequence row
        // is locked from here to the commit: a bill saved at a counter meanwhile waits for this
        // one, and one saved before the lock is seen here.
        if (backdated && await LaterBillExistsAsync(request.Payload.BillDate, now, cancellationToken))
        {
            throw new BillStateConflictException(
                $"A bill made after {now.ToOffset(Contracts.IndiaTime.Offset):dd MMM yyyy, HH:mm} is already saved, so a bill dated then would be numbered after it.");
        }

        var bill = NewPendingBill(billId, reservation, now);
        bill.CreateOperationId = request.ClientOperationId;

        var revision = BillSerialization.BuildRevision(bill.Id, request.Payload, revisionNo: 1, supersedes: null, createdAt: now);
        bill.CurrentRevisionId = revision.Id;

        dbContext.Bills.Add(bill);
        dbContext.BillRevisions.Add(revision);
        auditStore.Write(
            bill.Id,
            BillAuditEvents.PendingCreated,
            bill.State,
            now,
            new
            {
                invoiceNumber = reservation.FormattedNumber,
                fiscalYear = reservation.FiscalYear
            });
        await dbContext.SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return BillSerialization.MapResponse(bill, revision);
    }

    /// <summary>
    /// Whether a stored bill comes after a bill dated <paramref name="billDate"/> and made at
    /// <paramref name="at"/>: one of a later date, or one of the same date (or with no date)
    /// made in that minute or after. Two branches, so each can use an index (the revisions' date,
    /// the bills' creation time): it runs for every batch bill while counters wait on the lock.
    /// </summary>
    private Task<bool> LaterBillExistsAsync(DateOnly billDate, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var minute = new DateTimeOffset(at.Year, at.Month, at.Day, at.Hour, at.Minute, 0, at.Offset).ToUniversalTime();

        var datedLater = from rev in dbContext.BillRevisions.AsNoTracking()
                         where rev.BillDate > billDate
                         join stored in dbContext.Bills.AsNoTracking() on rev.BillId equals stored.Id
                         where stored.CurrentRevisionId == rev.Id
                         select stored.Id;

        var madeLater = from stored in dbContext.Bills.AsNoTracking()
                        where stored.CreatedAtUtc >= minute
                        join rev in dbContext.BillRevisions.AsNoTracking()
                            on stored.CurrentRevisionId equals rev.Id into revs
                        from rev in revs.DefaultIfEmpty()
                        where rev == null || rev.BillDate == null || rev.BillDate >= billDate
                        select stored.Id;

        return datedLater.Concat(madeLater).AnyAsync(cancellationToken);
    }

    /// <summary>
    /// Reserves the invoice number for a bill that is about to be created. The
    /// bill date decides the series: a 31 March bill entered on 1 April still
    /// belongs to the year that is closing.
    /// </summary>
    private Task<ReservedNumber> ReserveNumberAsync(DateOnly billDate, CancellationToken cancellationToken)
        => numberingService.ReserveAsync(
            InvoiceNumberFormatter.ComputeFiscalYear(billDate),
            cancellationToken);

    /// <summary>A new bill, pending, carrying the number just reserved for it.</summary>
    private static BillEntity NewPendingBill(
        Guid billId,
        ReservedNumber reservation,
        DateTimeOffset now) => new()
        {
            Id = billId,
            State = BillStates.Pending,
            InvoiceNumber = reservation.FormattedNumber,
            FiscalYear = reservation.FiscalYear,
            // Occupancy is read from this, never from the display text.
            InvoiceCore = reservation.ReservedValue,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

    /// <summary>
    /// The invoice number was reserved in the bill date's fiscal-year series.
    /// An edit that moves the date across 1 April would leave the bill in the
    /// wrong series, so it is refused; the operator creates a new bill instead.
    /// </summary>
    private static void EnsureBillDateMatchesFiscalYear(BillEntity bill, BillPayloadDto payload)
    {
        if (string.IsNullOrWhiteSpace(bill.FiscalYear))
        {
            return;
        }

        var dateFiscalYear = InvoiceNumberFormatter.ComputeFiscalYear(payload.BillDate);
        if (!string.Equals(dateFiscalYear, bill.FiscalYear, StringComparison.Ordinal))
        {
            throw new BillStateConflictException(
                $"Bill date {payload.BillDate:dd-MM-yyyy} falls in fiscal year {dateFiscalYear}, but invoice {bill.InvoiceNumber} is numbered in {bill.FiscalYear}. Keep a date in {bill.FiscalYear}, or create a new bill for the other year.");
        }
    }


}
