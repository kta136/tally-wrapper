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
    BillPayloadDto Payload,
    TallyPostOperation Operation = TallyPostOperation.Create,
    string? TargetTagName = null,
    string? TargetTagValue = null);

public enum TallyPostOperation
{
    Create,
    Alter,
}

public enum TallyPostOutcome
{
    Posted,
    Failed,
    Unknown,
}

public sealed record TallyPostResponse(
    TallyPostOutcome Outcome,
    string? RemoteId,
    string? ErrorCode,
    string? ErrorMessage,
    string? RequestExcerpt,
    string? ResponseExcerpt,
    string? TallyMasterId = null)
{
    /// <summary>Tally accepted the voucher.</summary>
    public static TallyPostResponse Posted(
        string? remoteId,
        string? requestExcerpt = null,
        string? responseExcerpt = null,
        string? tallyMasterId = null) =>
        new(TallyPostOutcome.Posted, remoteId, null, null, requestExcerpt, responseExcerpt, tallyMasterId);

    /// <summary>Tally definitely does not hold the voucher: it was rejected, or never sent.</summary>
    public static TallyPostResponse Failed(
        string errorCode,
        string? errorMessage,
        string? requestExcerpt = null,
        string? responseExcerpt = null) =>
        new(TallyPostOutcome.Failed, null, errorCode, errorMessage, requestExcerpt, responseExcerpt);

    /// <summary>Tally may or may not hold the voucher; the bill needs reconciliation.</summary>
    public static TallyPostResponse Unknown(
        string errorCode,
        string? errorMessage,
        string? requestExcerpt = null,
        string? responseExcerpt = null) =>
        new(TallyPostOutcome.Unknown, null, errorCode, errorMessage, requestExcerpt, responseExcerpt);
}
