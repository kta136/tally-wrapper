using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Application.Bills;
using ShowroomBilling.Application.Numbering;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Numbering;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Infrastructure.Persistence.Entities;

namespace ShowroomBilling.Infrastructure.Bills;

internal sealed class BillLifecycleWorkflow(
    ShowroomBillingDbContext dbContext,
    INumberingService numberingService,
    BillAuditStore auditStore)
{
    private const string DefaultShowroomCode = "default";
    private const string BillTypeSales = "sales";

    internal Task<BillResponse> CreateDraftAsync(
        CreateBillDraftRequest request,
        CancellationToken cancellationToken = default)
        => CreateDraftCoreAsync(request, DateTimeOffset.UtcNow, cancellationToken);

    internal Task<BillResponse> CreateBackdatedDraftAsync(
        CreateBillDraftRequest request,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken = default)
        => CreateDraftCoreAsync(request, createdAtUtc, cancellationToken);

    internal async Task<BillResponse> UpdateDraftAsync(
        Guid billId,
        UpdateBillDraftRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Payload);
        BillValidator.Validate(request.Payload);

        var bill = await LoadTrackedBillAsync(billId, cancellationToken);
        if (bill.State is BillStates.Posting or BillStates.ReconciliationRequired)
        {
            throw new BillStateConflictException(
                $"Bill '{billId}' cannot be edited while its Tally outcome is unresolved.");
        }
        EnsureBillDateMatchesFiscalYear(bill, request.Payload);
        if (bill.State is BillStates.Voided or BillStates.Revised)
        {
            throw new BillStateConflictException(
                $"Bill '{billId}' is in state '{bill.State}' and cannot be edited.");
        }

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

        var reopenFrom = bill.State is not (BillStates.Pending or BillStates.Draft)
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

        if (reopenFrom is not null)
        {
            bill.State = BillStates.Pending;
            bill.EditedAfterPush = inTally;
            auditStore.Write(
                bill.Id,
                "bill.edit.reopened",
                bill.State,
                now,
                new
                {
                    fromState = reopenFrom,
                    invoiceNumber = bill.InvoiceNumber,
                    revisionNo = revision.RevisionNo
                });
        }
        else
        {
            auditStore.Write(bill.Id, "bill.pending.updated", bill.State, now);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return BillSerialization.MapResponse(bill, revision);
    }

    internal async Task<BillResponse> ReviseAsync(
        Guid billId,
        ReviseBillRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var priorBill = await LoadTrackedBillAsync(billId, cancellationToken);
        if (priorBill.State is not (BillStates.Pending or BillStates.Draft))
        {
            throw new BillStateConflictException(
                $"Bill '{billId}' is in state '{priorBill.State}'; revise is currently only supported from 'pending'.");
        }

        var priorRevision = priorBill.CurrentRevisionId is Guid id
            ? await dbContext.BillRevisions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            : null;

        var payload = request.InitialPayload ?? (priorRevision is not null ? BillSerialization.DeserializePayload(priorRevision) : throw new BillStateConflictException(
            $"Bill '{billId}' has no current revision; cannot derive payload for revise."));

        if (request.InitialPayload is not null)
        {
            BillValidator.Validate(request.InitialPayload);
        }

        var now = DateTimeOffset.UtcNow;
        var newBillId = Guid.CreateVersion7();

        // The revised bill is a new bill and needs its own number, exactly
        // like a fresh draft. Reservation, new bill, supersession and audit
        // commit together so a failure leaves the original bill untouched.
        await using var transaction = UsesInMemoryProvider()
            ? null
            : await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var reservation = await numberingService.ReserveAsync(
            new ReserveNumberRequest(
                IdempotencyKey: $"draft:{newBillId:N}",
                DocumentType: INumberingService.DocumentTypeSalesInvoice,
                FiscalYear: InvoiceNumberFormatter.ComputeFiscalYear(payload.BillDate),
                ReservedForReference: newBillId.ToString()),
            cancellationToken);

        // A reservation retry clears the change tracker; re-attach the prior bill.
        priorBill = await LoadTrackedBillAsync(billId, cancellationToken);

        var newBill = new BillEntity
        {
            Id = newBillId,
            ShowroomId = priorBill.ShowroomId,
            CounterId = priorBill.CounterId,
            BillType = priorBill.BillType,
            State = BillStates.Pending,
            InvoiceNumber = reservation.FormattedNumber,
            FiscalYear = reservation.FiscalYear,
            InvoiceCore = reservation.ReservedValue,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        var newRevision = BillSerialization.BuildRevision(newBill.Id, payload, revisionNo: 1, supersedes: priorBill.CurrentRevisionId, createdAt: now);
        newBill.CurrentRevisionId = newRevision.Id;

        priorBill.State = BillStates.Revised;
        priorBill.SupersededByBillId = newBill.Id;
        priorBill.UpdatedAtUtc = now;

        dbContext.Bills.Add(newBill);
        dbContext.BillRevisions.Add(newRevision);
        auditStore.Write(priorBill.Id, "bill.revised", priorBill.State, now, new { supersededByBillId = newBill.Id });
        auditStore.Write(
            newBill.Id,
            "bill.pending.created",
            newBill.State,
            now,
            new
            {
                supersedesBillId = priorBill.Id,
                invoiceNumber = reservation.FormattedNumber,
                reservationId = reservation.ReservationId,
                fiscalYear = reservation.FiscalYear
            });
        await dbContext.SaveChangesAsync(cancellationToken);

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return BillSerialization.MapResponse(newBill, newRevision);
    }

    internal async Task<BillResponse> VoidAsync(
        Guid billId,
        VoidBillRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var bill = await LoadTrackedBillAsync(billId, cancellationToken);
        if (bill.State is not (BillStates.Pending or BillStates.Draft or BillStates.Failed))
        {
            throw new BillStateConflictException(
                $"Bill '{billId}' is in state '{bill.State}'; void is only allowed from pending/draft/failed in this phase.");
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
        bill.VoidedAtUtc = now;
        bill.UpdatedAtUtc = now;

        var reason = string.IsNullOrWhiteSpace(request.Reason) ? "unspecified" : request.Reason.Trim();
        auditStore.Write(bill.Id, "bill.voided", bill.State, now, new { reason });

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
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Payload);
        BillValidator.Validate(request.Payload);

        if (request.ClientOperationId is not Guid operationId)
        {
            return await CreateNewDraftAsync(request, now, cancellationToken);
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
            return await CreateNewDraftAsync(request, now, cancellationToken);
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

    private bool IsUniqueViolation(DbUpdateException exception) =>
        !UsesInMemoryProvider()
        && exception.GetBaseException() is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation };

    private async Task<BillResponse> CreateNewDraftAsync(
        CreateBillDraftRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var showroomId = ResolveShowroomId(DefaultShowroomCode);
        var billId = Guid.CreateVersion7();

        // Reservation + bill/revision/audit share one transaction so a failed
        // bill persist doesn't leave an orphaned reserved number behind.
        await using var transaction = UsesInMemoryProvider()
            ? null
            : await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var reservation = await numberingService.ReserveAsync(
            new ReserveNumberRequest(
                IdempotencyKey: $"draft:{billId:N}",
                DocumentType: INumberingService.DocumentTypeSalesInvoice,
                // The bill date decides the series: a 31 March bill entered on
                // 1 April still belongs to the year that is closing.
                FiscalYear: InvoiceNumberFormatter.ComputeFiscalYear(request.Payload.BillDate),
                ReservedForReference: billId.ToString()),
            cancellationToken);

        var bill = new BillEntity
        {
            Id = billId,
            ShowroomId = showroomId,
            CounterId = request.CounterId,
            BillType = BillTypeSales,
            State = BillStates.Pending,
            InvoiceNumber = reservation.FormattedNumber,
            FiscalYear = reservation.FiscalYear,
            InvoiceCore = reservation.ReservedValue,
            CreateOperationId = request.ClientOperationId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        var revision = BillSerialization.BuildRevision(bill.Id, request.Payload, revisionNo: 1, supersedes: null, createdAt: now);
        bill.CurrentRevisionId = revision.Id;

        dbContext.Bills.Add(bill);
        dbContext.BillRevisions.Add(revision);
        auditStore.Write(
            bill.Id,
            "bill.pending.created",
            bill.State,
            now,
            new
            {
                invoiceNumber = reservation.FormattedNumber,
                reservationId = reservation.ReservationId,
                fiscalYear = reservation.FiscalYear
            });
        await dbContext.SaveChangesAsync(cancellationToken);

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return BillSerialization.MapResponse(bill, revision);
    }

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

    private async Task<BillEntity> LoadTrackedBillAsync(Guid billId, CancellationToken cancellationToken)
    {
        var bill = await dbContext.Bills.FirstOrDefaultAsync(x => x.Id == billId, cancellationToken);
        if (bill is null)
        {
            throw new BillNotFoundException(billId);
        }
        return bill;
    }

    private static Guid ResolveShowroomId(string showroomCode)
    {
        var bytes = Encoding.UTF8.GetBytes(showroomCode.Trim().ToLowerInvariant());
        var hash = MD5.HashData(bytes);
        return new Guid(hash);
    }

    private bool UsesInMemoryProvider() =>
        string.Equals(dbContext.Database.ProviderName, "Microsoft.EntityFrameworkCore.InMemory", StringComparison.Ordinal);
}
