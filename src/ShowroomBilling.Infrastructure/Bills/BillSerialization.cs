using System.Text.Json;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Infrastructure.Persistence.Entities;

namespace ShowroomBilling.Infrastructure.Bills;

internal static class BillSerialization
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    internal static BillRevisionEntity BuildRevision(
        Guid billId,
        BillPayloadDto payload,
        int revisionNo,
        Guid? supersedes,
        DateTimeOffset createdAt)
    {
        return new BillRevisionEntity
        {
            Id = Guid.NewGuid(),
            BillId = billId,
            RevisionNo = revisionNo,
            SnapshotJson = JsonSerializer.Serialize(payload, JsonOptions),
            TotalsJson = JsonSerializer.Serialize(payload.Totals, JsonOptions),
            PartyName = string.IsNullOrWhiteSpace(payload.PartyName) ? null : payload.PartyName.Trim(),
            BillDate = payload.BillDate,
            GrandTotal = payload.Totals.GrandTotal,
            SupersedesRevisionId = supersedes,
            CreatedAtUtc = createdAt
        };
    }

    internal static BillPayloadDto DeserializePayload(BillRevisionEntity revision)
    {
        var payload = JsonSerializer.Deserialize<BillPayloadDto>(revision.SnapshotJson, JsonOptions);
        return payload ?? throw new InvalidOperationException(
            $"Revision '{revision.Id}' has an unreadable snapshot payload.");
    }

    internal static BillResponse MapResponse(BillEntity bill, BillRevisionEntity? currentRevision)
    {
        BillRevisionResponse? revisionResponse = null;
        if (currentRevision is not null)
        {
            var payload = DeserializePayload(currentRevision);
            revisionResponse = new BillRevisionResponse(
                Id: currentRevision.Id,
                RevisionNo: currentRevision.RevisionNo,
                CreatedAtUtc: currentRevision.CreatedAtUtc,
                SubmittedAtUtc: currentRevision.SubmittedAtUtc,
                FinalizedAtUtc: currentRevision.FinalizedAtUtc,
                SupersedesRevisionId: currentRevision.SupersedesRevisionId,
                Payload: payload);
        }

        return new BillResponse(
            Id: bill.Id,
            ShowroomId: bill.ShowroomId,
            CounterId: bill.CounterId,
            BillType: bill.BillType,
            State: bill.State,
            InvoiceNumber: bill.InvoiceNumber,
            FiscalYear: bill.FiscalYear,
            SupersededByBillId: bill.SupersededByBillId,
            CurrentRevisionId: bill.CurrentRevisionId,
            CreatedAtUtc: bill.CreatedAtUtc,
            UpdatedAtUtc: bill.UpdatedAtUtc,
            VoidedAtUtc: bill.VoidedAtUtc,
            CurrentRevision: revisionResponse,
            EditedAfterPush: bill.EditedAfterPush);
    }
}
