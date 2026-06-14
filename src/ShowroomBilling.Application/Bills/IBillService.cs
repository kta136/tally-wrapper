using ShowroomBilling.Contracts.Bills;

namespace ShowroomBilling.Application.Bills;

public interface IBillService
{
    // Kept for backwards-compat with callers that reference these through the
    // interface; new code should use ShowroomBilling.Contracts.Bills.BillStates.
    public const string StatePending = BillStates.Pending;
    public const string StatePosting = BillStates.Posting;
    public const string StatePosted = BillStates.Posted;
    public const string StateFailed = BillStates.Failed;
    public const string StateRevised = BillStates.Revised;
    public const string StateVoided = BillStates.Voided;

    Task<BillResponse> CreateDraftAsync(
        CreateBillDraftRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a draft bill whose <c>CreatedAtUtc</c> / <c>UpdatedAtUtc</c> /
    /// revision <c>CreatedAtUtc</c> / audit event timestamp are all set to
    /// <paramref name="createdAtUtc"/> instead of <see cref="DateTimeOffset.UtcNow"/>.
    /// Used by the synthetic batch planner to produce bills with backfilled
    /// audit timestamps spread across a historical time window.
    /// Invoice-number reservation still happens at real-now (numbering is monotonic
    /// and independent of the bill's business date).
    /// </summary>
    Task<BillResponse> CreateBackdatedDraftAsync(
        CreateBillDraftRequest request,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken = default);

    Task<BillResponse> UpdateDraftAsync(
        Guid billId,
        UpdateBillDraftRequest request,
        CancellationToken cancellationToken = default);

    Task<BillResponse?> GetAsync(Guid billId, CancellationToken cancellationToken = default);

    Task<BillBatchGetResponse> GetManyAsync(
        BillBatchGetRequest request,
        CancellationToken cancellationToken = default);

    Task<BillListResponse> SearchAsync(BillSearchFilter filter, CancellationToken cancellationToken = default);

    Task<BillResponse> PushAsync(
        Guid billId,
        PushBillRequest request,
        CancellationToken cancellationToken = default);

    Task<BillBatchPushResponse> PushSelectedAsync(
        PushSelectedBillsRequest request,
        CancellationToken cancellationToken = default);

    Task<BillBatchPushResponse> PushPendingAsync(
        PushPendingBillsRequest request,
        CancellationToken cancellationToken = default);

    Task<BillResponse> ReviseAsync(
        Guid billId,
        ReviseBillRequest request,
        CancellationToken cancellationToken = default);

    Task<BillResponse> VoidAsync(
        Guid billId,
        VoidBillRequest request,
        CancellationToken cancellationToken = default);

    Task<BillAuditResponse?> GetAuditAsync(
        Guid billId,
        CancellationToken cancellationToken = default);

    Task<BillPostingStatusResponse?> GetPostingStatusAsync(
        Guid billId,
        CancellationToken cancellationToken = default);

    Task<BillPostingStatusResponse> RetryAsync(
        Guid billId,
        RetryBillPostingRequest request,
        CancellationToken cancellationToken = default);

    Task<BillPostingStatusResponse> RepostAsync(
        Guid billId,
        RepostBillRequest request,
        CancellationToken cancellationToken = default);

    Task<ChangeBillNumberResponse> ChangeInvoiceNumberAsync(
        Guid billId,
        ChangeBillNumberRequest request,
        CancellationToken cancellationToken = default);

    Task<BillResponse> MarkPostedAsync(
        Guid billId,
        MarkBillStateRequest request,
        CancellationToken cancellationToken = default);

    Task<BillResponse> MarkPendingAsync(
        Guid billId,
        MarkBillStateRequest request,
        CancellationToken cancellationToken = default);

    Task<DeleteBillResponse> DeleteAsync(
        Guid billId,
        DeleteBillRequest request,
        CancellationToken cancellationToken = default);

    Task<DeleteSelectedBillsResponse> DeleteSelectedAsync(
        DeleteSelectedBillsRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class BillStateConflictException(string message) : InvalidOperationException(message);

public sealed class TallyPreflightUnavailableException(string message) : InvalidOperationException(message);

public sealed class BillNotFoundException(Guid billId) : Exception($"Bill '{billId}' was not found.");

public sealed class BillValidationException : Exception
{
    public IReadOnlyList<string> Errors { get; }

    public BillValidationException(IEnumerable<string> errors)
        : base(FormatMessage(errors))
    {
        Errors = errors.ToArray();
    }

    private static string FormatMessage(IEnumerable<string> errors)
    {
        var list = errors.ToArray();
        return list.Length switch
        {
            0 => "Bill payload is invalid.",
            1 => list[0],
            _ => "Bill payload is invalid: " + string.Join("; ", list)
        };
    }
}
