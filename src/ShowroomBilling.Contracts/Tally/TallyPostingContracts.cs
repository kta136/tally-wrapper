using ShowroomBilling.Contracts.Bills;

namespace ShowroomBilling.Contracts.Tally;

/// <summary>
/// Synchronous "post this bill to Tally now" request used by BillPostingWorkflow.PushAsync.
/// There is no queue — the request is fulfilled inside the push HTTP call.
/// An Alter names its voucher by <paramref name="TargetTagName"/> / <paramref name="TargetTagValue"/>
/// and <paramref name="TargetVoucherDate"/>, the date that voucher has in Tally now
/// (the bill's own date may have been edited since). <paramref name="TargetCompany"/> and
/// <paramref name="TargetVoucherType"/> are where that voucher was read: an Alter is not
/// sent anywhere else, even when Settings change in between.
/// </summary>
public sealed record TallyPostRequest(
    Guid BillId,
    string InvoiceNumber,
    string IdempotencyKey,
    BillPayloadDto Payload,
    TallyPostOperation Operation = TallyPostOperation.Create,
    string? TargetTagName = null,
    string? TargetTagValue = null,
    DateOnly? TargetVoucherDate = null,
    string? TargetCompany = null,
    string? TargetVoucherType = null);

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

/// <param name="Company">
/// The Tally company the voucher went to, on a voucher Tally accepted. A master
/// id names a voucher only within its company, so an Alter must go to the same one.
/// </param>
/// <param name="UpdatedExistingVoucher">
/// A create that Tally answered by changing a voucher it already had: one made
/// earlier with the same <c>REMOTEID</c>, whose answer was lost or which was
/// sent again. TallyPrime 7.2 updates that voucher rather than making a second.
/// </param>
public sealed record TallyPostResponse(
    TallyPostOutcome Outcome,
    string? RemoteId,
    string? ErrorCode,
    string? ErrorMessage,
    string? RequestExcerpt,
    string? ResponseExcerpt,
    string? TallyMasterId = null,
    string? Company = null,
    bool UpdatedExistingVoucher = false)
{
    /// <summary>Tally accepted the voucher.</summary>
    public static TallyPostResponse Posted(
        string? remoteId,
        string? requestExcerpt = null,
        string? responseExcerpt = null,
        string? tallyMasterId = null,
        bool updatedExistingVoucher = false) =>
        new(TallyPostOutcome.Posted, remoteId, null, null, requestExcerpt, responseExcerpt, tallyMasterId,
            UpdatedExistingVoucher: updatedExistingVoucher);

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
