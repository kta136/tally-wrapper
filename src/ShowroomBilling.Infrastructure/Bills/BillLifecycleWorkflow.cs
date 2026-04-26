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
        if (bill.State is IBillService.StatePosting)
        {
            throw new BillStateConflictException(
                $"Bill '{billId}' is being posted to Tally right now; wait for the current job to settle before editing.");
        }
        if (bill.State is IBillService.StateVoided or IBillService.StateRevised)
        {
            throw new BillStateConflictException(
                $"Bill '{billId}' is in state '{bill.State}' and cannot be edited.");
        }

        var reopenFrom = bill.State is not (IBillService.StatePending or BillStates.Draft)
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

        if (reopenFrom is not null)
        {
            bill.State = IBillService.StatePending;
            bill.EditedAfterPush = true;
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
        if (priorBill.State is not (IBillService.StatePending or BillStates.Draft))
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
        var newBill = new BillEntity
        {
            Id = Guid.NewGuid(),
            ShowroomId = priorBill.ShowroomId,
            CounterId = priorBill.CounterId,
            BillType = priorBill.BillType,
            State = IBillService.StatePending,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        var newRevision = BillSerialization.BuildRevision(newBill.Id, payload, revisionNo: 1, supersedes: priorBill.CurrentRevisionId, createdAt: now);
        newBill.CurrentRevisionId = newRevision.Id;

        priorBill.State = IBillService.StateRevised;
        priorBill.SupersededByBillId = newBill.Id;
        priorBill.UpdatedAtUtc = now;

        dbContext.Bills.Add(newBill);
        dbContext.BillRevisions.Add(newRevision);
        auditStore.Write(priorBill.Id, "bill.revised", priorBill.State, now, new { supersededByBillId = newBill.Id });
        auditStore.Write(newBill.Id, "bill.pending.created", newBill.State, now, new { supersedesBillId = priorBill.Id });
        await dbContext.SaveChangesAsync(cancellationToken);

        return BillSerialization.MapResponse(newBill, newRevision);
    }

    internal async Task<BillResponse> VoidAsync(
        Guid billId,
        VoidBillRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var bill = await LoadTrackedBillAsync(billId, cancellationToken);
        if (bill.State is not (IBillService.StatePending or BillStates.Draft or IBillService.StateFailed))
        {
            throw new BillStateConflictException(
                $"Bill '{billId}' is in state '{bill.State}'; void is only allowed from pending/draft/failed in this phase.");
        }

        var now = DateTimeOffset.UtcNow;
        bill.State = IBillService.StateVoided;
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

        var showroomId = ResolveShowroomId(DefaultShowroomCode);
        var billId = Guid.NewGuid();

        // Reservation + bill/revision/audit share one transaction so a failed
        // bill persist doesn't leave an orphaned reserved number behind.
        await using var transaction = UsesInMemoryProvider()
            ? null
            : await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var reservation = await numberingService.ReserveAsync(
            new ReserveNumberRequest(
                IdempotencyKey: $"draft:{billId:N}",
                DocumentType: INumberingService.DocumentTypeSalesInvoice,
                FiscalYear: null,
                ReservedForReference: billId.ToString()),
            cancellationToken);

        var bill = new BillEntity
        {
            Id = billId,
            ShowroomId = showroomId,
            CounterId = request.CounterId,
            BillType = BillTypeSales,
            State = IBillService.StatePending,
            InvoiceNumber = reservation.FormattedNumber,
            FiscalYear = reservation.FiscalYear,
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
