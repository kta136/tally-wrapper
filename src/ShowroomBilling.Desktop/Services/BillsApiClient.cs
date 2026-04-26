using System.Net.Http;
using System.Net.Http.Json;
using ShowroomBilling.Contracts.Admin;
using ShowroomBilling.Contracts.Bills;

namespace ShowroomBilling.Desktop.Services;

public sealed class BillsApiClient(HttpClient httpClient) : IBillsApiClient
{
    public async Task<BillResponse> CreateDraftAsync(CreateBillDraftRequest request, CancellationToken cancellationToken = default)
    {
        var response = await httpClient.PostAsJsonAsync("/api/bills/drafts", request, cancellationToken);
        await ApiResponseReader.EnsureSuccessOrThrowAsync(response, cancellationToken);
        return await ReadAsync(response, cancellationToken);
    }

    public async Task<BillResponse> UpdateDraftAsync(Guid billId, UpdateBillDraftRequest request, CancellationToken cancellationToken = default)
    {
        var response = await httpClient.PutAsJsonAsync($"/api/bills/drafts/{billId}", request, cancellationToken);
        await ApiResponseReader.EnsureSuccessOrThrowAsync(response, cancellationToken);
        return await ReadAsync(response, cancellationToken);
    }

    public async Task<BillResponse> PushAsync(Guid billId, PushBillRequest request, CancellationToken cancellationToken = default)
    {
        var response = await httpClient.PostAsJsonAsync($"/api/bills/{billId}/push", request, cancellationToken);
        await ApiResponseReader.EnsureSuccessOrThrowAsync(response, cancellationToken);
        return await ReadAsync(response, cancellationToken);
    }

    public async Task<BillBatchPushResponse> PushSelectedAsync(PushSelectedBillsRequest request, CancellationToken cancellationToken = default)
    {
        var response = await httpClient.PostAsJsonAsync("/api/bills/push-selected", request, cancellationToken);
        await ApiResponseReader.EnsureSuccessOrThrowAsync(response, cancellationToken);
        return await ReadBatchAsync(response, cancellationToken);
    }

    public async Task<BillBatchPushResponse> PushPendingAsync(PushPendingBillsRequest request, CancellationToken cancellationToken = default)
    {
        var response = await httpClient.PostAsJsonAsync("/api/bills/push-pending", request, cancellationToken);
        await ApiResponseReader.EnsureSuccessOrThrowAsync(response, cancellationToken);
        return await ReadBatchAsync(response, cancellationToken);
    }

    public async Task<BillListResponse> SearchAsync(BillSearchFilter filter, CancellationToken cancellationToken = default)
    {
        var qp = new List<string>();
        if (!string.IsNullOrWhiteSpace(filter.State)) qp.Add($"state={Uri.EscapeDataString(filter.State!)}");
        if (filter.FromDate is not null) qp.Add($"fromDate={filter.FromDate:yyyy-MM-dd}");
        if (filter.ToDate is not null) qp.Add($"toDate={filter.ToDate:yyyy-MM-dd}");
        if (filter.Skip is not null) qp.Add($"skip={filter.Skip}");
        if (filter.Take is not null) qp.Add($"take={filter.Take}");
        if (!string.IsNullOrWhiteSpace(filter.Sort)) qp.Add($"sort={Uri.EscapeDataString(filter.Sort!)}");
        if (filter.IncludeTotal is not null) qp.Add($"includeTotal={filter.IncludeTotal.Value.ToString().ToLowerInvariant()}");
        var url = qp.Count == 0 ? "/api/bills" : $"/api/bills?{string.Join('&', qp)}";
        var body = await httpClient.GetFromJsonAsync<BillListResponse>(url, cancellationToken);
        return body ?? throw new InvalidOperationException("Bills API returned an empty list payload.");
    }

    public async Task<BillResponse?> GetAsync(Guid billId, CancellationToken cancellationToken = default)
    {
        var response = await httpClient.GetAsync($"/api/bills/{billId}", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        await ApiResponseReader.EnsureSuccessOrThrowAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<BillResponse>(cancellationToken: cancellationToken);
    }

    public async Task<BillBatchGetResponse> GetManyAsync(BillBatchGetRequest request, CancellationToken cancellationToken = default)
    {
        var response = await httpClient.PostAsJsonAsync("/api/bills/batch", request, cancellationToken);
        await ApiResponseReader.EnsureSuccessOrThrowAsync(response, cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<BillBatchGetResponse>(cancellationToken: cancellationToken);
        return body ?? throw new InvalidOperationException("Bills API returned an empty batch get payload.");
    }

    public async Task<BillAuditResponse?> GetAuditAsync(Guid billId, CancellationToken cancellationToken = default)
    {
        var response = await httpClient.GetAsync($"/api/bills/{billId}/audit", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        await ApiResponseReader.EnsureSuccessOrThrowAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<BillAuditResponse>(cancellationToken: cancellationToken);
    }

    public async Task<BillPostingStatusResponse?> GetPostingStatusAsync(Guid billId, CancellationToken cancellationToken = default)
    {
        var response = await httpClient.GetAsync($"/api/bills/{billId}/posting-status", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        await ApiResponseReader.EnsureSuccessOrThrowAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<BillPostingStatusResponse>(cancellationToken: cancellationToken);
    }

    public async Task<BillPostingStatusResponse> RetryAsync(Guid billId, RetryBillPostingRequest request, CancellationToken cancellationToken = default)
    {
        var response = await httpClient.PostAsJsonAsync($"/api/bills/{billId}/retry", request, cancellationToken);
        await ApiResponseReader.EnsureSuccessOrThrowAsync(response, cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<BillPostingStatusResponse>(cancellationToken: cancellationToken);
        return body ?? throw new InvalidOperationException("Bills API returned an empty retry payload.");
    }

    public async Task<BillPostingStatusResponse> RepostAsync(Guid billId, RepostBillRequest request, CancellationToken cancellationToken = default)
    {
        var response = await httpClient.PostAsJsonAsync($"/api/bills/{billId}/repost", request, cancellationToken);
        await ApiResponseReader.EnsureSuccessOrThrowAsync(response, cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<BillPostingStatusResponse>(cancellationToken: cancellationToken);
        return body ?? throw new InvalidOperationException("Bills API returned an empty repost payload.");
    }

    public async Task<BillResponse> VoidAsync(Guid billId, VoidBillRequest request, CancellationToken cancellationToken = default)
    {
        var response = await httpClient.PostAsJsonAsync($"/api/bills/{billId}/void", request, cancellationToken);
        await ApiResponseReader.EnsureSuccessOrThrowAsync(response, cancellationToken);
        return await ReadAsync(response, cancellationToken);
    }

    public async Task<BillResponse> ReviseAsync(Guid billId, ReviseBillRequest request, CancellationToken cancellationToken = default)
    {
        var response = await httpClient.PostAsJsonAsync($"/api/bills/{billId}/revise", request, cancellationToken);
        await ApiResponseReader.EnsureSuccessOrThrowAsync(response, cancellationToken);
        return await ReadAsync(response, cancellationToken);
    }

    public async Task<ChangeBillNumberResponse> ChangeInvoiceNumberAsync(Guid billId, ChangeBillNumberRequest request, string adminToken, CancellationToken cancellationToken = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/bills/{billId}/change-number")
        {
            Content = JsonContent.Create(request)
        };
        req.Headers.Add(AdminTokenConstants.HeaderName, adminToken);
        var response = await httpClient.SendAsync(req, cancellationToken);
        await ApiResponseReader.EnsureSuccessOrThrowAsync(response, cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<ChangeBillNumberResponse>(cancellationToken: cancellationToken);
        return body ?? throw new InvalidOperationException("Empty change-number response.");
    }

    public async Task<BillResponse> MarkPostedAsync(Guid billId, MarkBillStateRequest request, string adminToken, CancellationToken cancellationToken = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/bills/{billId}/mark-posted")
        {
            Content = JsonContent.Create(request)
        };
        req.Headers.Add(AdminTokenConstants.HeaderName, adminToken);
        var response = await httpClient.SendAsync(req, cancellationToken);
        await ApiResponseReader.EnsureSuccessOrThrowAsync(response, cancellationToken);
        return await ReadAsync(response, cancellationToken);
    }

    public async Task<BillResponse> MarkPendingAsync(Guid billId, MarkBillStateRequest request, string adminToken, CancellationToken cancellationToken = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/bills/{billId}/mark-pending")
        {
            Content = JsonContent.Create(request)
        };
        req.Headers.Add(AdminTokenConstants.HeaderName, adminToken);
        var response = await httpClient.SendAsync(req, cancellationToken);
        await ApiResponseReader.EnsureSuccessOrThrowAsync(response, cancellationToken);
        return await ReadAsync(response, cancellationToken);
    }

    public async Task<DeleteBillResponse> DeleteAsync(Guid billId, DeleteBillRequest request, string adminToken, CancellationToken cancellationToken = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"/api/bills/{billId}")
        {
            Content = JsonContent.Create(request)
        };
        req.Headers.Add(AdminTokenConstants.HeaderName, adminToken);
        var response = await httpClient.SendAsync(req, cancellationToken);
        await ApiResponseReader.EnsureSuccessOrThrowAsync(response, cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<DeleteBillResponse>(cancellationToken: cancellationToken);
        return body ?? throw new InvalidOperationException("Empty delete response.");
    }

    public async Task<DeleteSelectedBillsResponse> DeleteSelectedAsync(DeleteSelectedBillsRequest request, string adminToken, CancellationToken cancellationToken = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/bills/delete-selected")
        {
            Content = JsonContent.Create(request)
        };
        req.Headers.Add(AdminTokenConstants.HeaderName, adminToken);
        var response = await httpClient.SendAsync(req, cancellationToken);
        await ApiResponseReader.EnsureSuccessOrThrowAsync(response, cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<DeleteSelectedBillsResponse>(cancellationToken: cancellationToken);
        return body ?? throw new InvalidOperationException("Empty delete-selected response.");
    }

    public async Task<SyntheticBatchResponse> CreateSyntheticBatchAsync(SyntheticBatchRequest request, string adminToken, CancellationToken cancellationToken = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/bills/synthetic-batch")
        {
            Content = JsonContent.Create(request)
        };
        req.Headers.Add(AdminTokenConstants.HeaderName, adminToken);
        var response = await httpClient.SendAsync(req, cancellationToken);
        return await ApiResponseReader.ReadOrThrowAsync<SyntheticBatchResponse>(response, cancellationToken);
    }

    private static async Task<BillResponse> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadFromJsonAsync<BillResponse>(cancellationToken: cancellationToken);
        return body ?? throw new InvalidOperationException("Bills API returned an empty payload.");
    }

    private static async Task<BillBatchPushResponse> ReadBatchAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadFromJsonAsync<BillBatchPushResponse>(cancellationToken: cancellationToken);
        return body ?? throw new InvalidOperationException("Bills API returned an empty batch payload.");
    }
}
