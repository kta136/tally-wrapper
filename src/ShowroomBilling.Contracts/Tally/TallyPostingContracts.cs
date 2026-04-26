using ShowroomBilling.Contracts.Bills;

namespace ShowroomBilling.Contracts.Tally;

/// <summary>
/// Synchronous "post this bill to Tally now" request used by BillService.PushAsync.
/// There is no queue — the request is fulfilled inside the push HTTP call.
/// </summary>
public sealed record TallyPostRequest(
    Guid BillId,
    Guid RevisionId,
    int RevisionNo,
    string BillType,
    string InvoiceNumber,
    string FiscalYear,
    string IdempotencyKey,
    BillPayloadDto Payload);

public enum TallyPostOutcome
{
    Posted,
    Failed,
}

public sealed record TallyPostResponse(
    TallyPostOutcome Outcome,
    string? RemoteId,
    string? ErrorCode,
    string? ErrorMessage,
    string? XmlShape,
    string? RequestExcerpt,
    string? ResponseExcerpt);
