using System.Net.Http;
using ShowroomBilling.Contracts.Bills;

namespace ShowroomBilling.Desktop.Services;

public sealed class BillsApiClient(HttpClient httpClient) : IBillsApiClient
{
    public Task<BillResponse> CreateDraftAsync(CreateBillDraftRequest request, CancellationToken cancellationToken = default)
        => httpClient.PostApiAsync<BillResponse>("/api/bills/drafts", request, cancellationToken);

    public Task<BillResponse> UpdateDraftAsync(Guid billId, UpdateBillDraftRequest request, CancellationToken cancellationToken = default)
        => httpClient.PutApiAsync<BillResponse>($"/api/bills/drafts/{billId}", request, cancellationToken);

    public Task<BillResponse> PushAsync(Guid billId, PushBillRequest request, CancellationToken cancellationToken = default)
        => httpClient.PostApiAsync<BillResponse>($"/api/bills/{billId}/push", request, cancellationToken);

    public Task<BillBatchPushResponse> PushSelectedAsync(PushSelectedBillsRequest request, CancellationToken cancellationToken = default)
        => httpClient.PostApiAsync<BillBatchPushResponse>("/api/bills/push-selected", request, cancellationToken);

    public Task<BillBatchPushResponse> PushPendingAsync(PushPendingBillsRequest request, CancellationToken cancellationToken = default)
        => httpClient.PostApiAsync<BillBatchPushResponse>("/api/bills/push-pending", request, cancellationToken);

    public Task<BillListResponse> SearchAsync(BillSearchFilter filter, CancellationToken cancellationToken = default)
    {
        var qp = new List<string>();
        if (!string.IsNullOrWhiteSpace(filter.State)) qp.Add($"state={Uri.EscapeDataString(filter.State!)}");
        if (filter.FromDate is not null) qp.Add($"fromDate={filter.FromDate:yyyy-MM-dd}");
        if (filter.ToDate is not null) qp.Add($"toDate={filter.ToDate:yyyy-MM-dd}");
        if (filter.Skip is not null) qp.Add($"skip={filter.Skip}");
        if (filter.Take is not null) qp.Add($"take={filter.Take}");
        if (!string.IsNullOrWhiteSpace(filter.Sort)) qp.Add($"sort={Uri.EscapeDataString(filter.Sort!)}");
        if (filter.IncludeTotal is not null) qp.Add($"includeTotal={filter.IncludeTotal.Value.ToString().ToLowerInvariant()}");
        if (!string.IsNullOrWhiteSpace(filter.Search)) qp.Add($"search={Uri.EscapeDataString(filter.Search!)}");
        var url = qp.Count == 0 ? "/api/bills" : $"/api/bills?{string.Join('&', qp)}";
        return httpClient.GetApiAsync<BillListResponse>(url, cancellationToken);
    }

    public Task<BillResponse?> GetAsync(Guid billId, CancellationToken cancellationToken = default)
        => httpClient.GetApiOrNullAsync<BillResponse>($"/api/bills/{billId}", cancellationToken);

    public Task<BillBatchGetResponse> GetManyAsync(BillBatchGetRequest request, CancellationToken cancellationToken = default)
        => httpClient.PostApiAsync<BillBatchGetResponse>("/api/bills/batch", request, cancellationToken);

    public Task<BillAuditResponse?> GetAuditAsync(Guid billId, CancellationToken cancellationToken = default)
        => httpClient.GetApiOrNullAsync<BillAuditResponse>($"/api/bills/{billId}/audit", cancellationToken);

    public Task<BillPostingStatusResponse?> GetPostingStatusAsync(Guid billId, CancellationToken cancellationToken = default)
        => httpClient.GetApiOrNullAsync<BillPostingStatusResponse>($"/api/bills/{billId}/posting-status", cancellationToken);

    public Task<BillPostingStatusResponse> RetryAsync(Guid billId, RetryBillPostingRequest request, CancellationToken cancellationToken = default)
        => httpClient.PostApiAsync<BillPostingStatusResponse>($"/api/bills/{billId}/retry", request, cancellationToken);

    public Task<BillPostingStatusResponse> RepostAsync(Guid billId, RepostBillRequest request, CancellationToken cancellationToken = default)
        => httpClient.PostApiAsync<BillPostingStatusResponse>($"/api/bills/{billId}/repost", request, cancellationToken);

    public Task<BillResponse> VoidAsync(Guid billId, VoidBillRequest request, CancellationToken cancellationToken = default)
        => httpClient.PostApiAsync<BillResponse>($"/api/bills/{billId}/void", request, cancellationToken);

    public Task<BillResponse> ReviseAsync(Guid billId, ReviseBillRequest request, CancellationToken cancellationToken = default)
        => httpClient.PostApiAsync<BillResponse>($"/api/bills/{billId}/revise", request, cancellationToken);

    public Task<ChangeBillNumberResponse> ChangeInvoiceNumberAsync(Guid billId, ChangeBillNumberRequest request, string adminToken, CancellationToken cancellationToken = default)
        => httpClient.PostApiAsync<ChangeBillNumberResponse>($"/api/bills/{billId}/change-number", request, cancellationToken, adminToken);

    public Task<BillResponse> MarkPostedAsync(Guid billId, MarkBillStateRequest request, string adminToken, CancellationToken cancellationToken = default)
        => httpClient.PostApiAsync<BillResponse>($"/api/bills/{billId}/mark-posted", request, cancellationToken, adminToken);

    public Task<BillResponse> MarkPendingAsync(Guid billId, MarkBillStateRequest request, string adminToken, CancellationToken cancellationToken = default)
        => httpClient.PostApiAsync<BillResponse>($"/api/bills/{billId}/mark-pending", request, cancellationToken, adminToken);

    public Task<DeleteBillResponse> DeleteAsync(Guid billId, DeleteBillRequest request, string adminToken, CancellationToken cancellationToken = default)
        => httpClient.SendApiAsync<DeleteBillResponse>(HttpMethod.Delete, $"/api/bills/{billId}", request, cancellationToken, adminToken);

    public Task<DeleteSelectedBillsResponse> DeleteSelectedAsync(DeleteSelectedBillsRequest request, string adminToken, CancellationToken cancellationToken = default)
        => httpClient.PostApiAsync<DeleteSelectedBillsResponse>("/api/bills/delete-selected", request, cancellationToken, adminToken);

    public Task<SyntheticBatchResponse> CreateSyntheticBatchAsync(SyntheticBatchRequest request, string adminToken, CancellationToken cancellationToken = default)
        => httpClient.PostApiAsync<SyntheticBatchResponse>("/api/bills/synthetic-batch", request, cancellationToken, adminToken);
}
