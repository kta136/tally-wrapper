using ShowroomBilling.Contracts.Bills;

namespace ShowroomBilling.Desktop.Services;

public interface IBillsApiClient
{
    Task<BillResponse> CreateDraftAsync(CreateBillDraftRequest request, CancellationToken cancellationToken = default);
    Task<BillResponse> UpdateDraftAsync(Guid billId, UpdateBillDraftRequest request, CancellationToken cancellationToken = default);
    Task<BillResponse> PushAsync(Guid billId, PushBillRequest request, CancellationToken cancellationToken = default);
    Task<BillBatchPushResponse> PushSelectedAsync(PushSelectedBillsRequest request, CancellationToken cancellationToken = default);
    Task<BillBatchPushResponse> PushPendingAsync(PushPendingBillsRequest request, CancellationToken cancellationToken = default);
    Task<BillListResponse> SearchAsync(BillSearchFilter filter, CancellationToken cancellationToken = default);
    Task<BillResponse?> GetAsync(Guid billId, CancellationToken cancellationToken = default);
    Task<BillBatchGetResponse> GetManyAsync(BillBatchGetRequest request, CancellationToken cancellationToken = default);
    Task<BillAuditResponse?> GetAuditAsync(Guid billId, CancellationToken cancellationToken = default);
    Task<BillPostingStatusResponse?> GetPostingStatusAsync(Guid billId, CancellationToken cancellationToken = default);
    Task<BillPostingStatusResponse> RetryAsync(Guid billId, RetryBillPostingRequest request, CancellationToken cancellationToken = default);
    Task<BillPostingStatusResponse> RepostAsync(Guid billId, RepostBillRequest request, CancellationToken cancellationToken = default);
    Task<BillResponse> VoidAsync(Guid billId, VoidBillRequest request, CancellationToken cancellationToken = default);
    Task<BillResponse> ReviseAsync(Guid billId, ReviseBillRequest request, CancellationToken cancellationToken = default);
    Task<ChangeBillNumberResponse> ChangeInvoiceNumberAsync(Guid billId, ChangeBillNumberRequest request, string adminToken, CancellationToken cancellationToken = default);
    Task<BillResponse> MarkPostedAsync(Guid billId, MarkBillStateRequest request, string adminToken, CancellationToken cancellationToken = default);
    Task<BillResponse> MarkPendingAsync(Guid billId, MarkBillStateRequest request, string adminToken, CancellationToken cancellationToken = default);
    Task<DeleteBillResponse> DeleteAsync(Guid billId, DeleteBillRequest request, string adminToken, CancellationToken cancellationToken = default);
    Task<DeleteSelectedBillsResponse> DeleteSelectedAsync(DeleteSelectedBillsRequest request, string adminToken, CancellationToken cancellationToken = default);
    Task<SyntheticBatchResponse> CreateSyntheticBatchAsync(SyntheticBatchRequest request, string adminToken, CancellationToken cancellationToken = default);
}
