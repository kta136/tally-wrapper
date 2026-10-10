using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using ShowroomBilling.Contracts.Leases;

namespace ShowroomBilling.Desktop.Services;

public sealed class DraftLeaseApiClient(HttpClient httpClient) : IDraftLeaseApiClient
{
    public async Task<DraftLeaseAcquireResult> AcquireAsync(
        DraftLeaseAcquireRequest request,
        CancellationToken cancellationToken = default)
    {
        using var message = ApiHttpClientExtensions.CreateRequest(HttpMethod.Post, "/api/leases/drafts/acquire", request);
        using var http = await httpClient.SendAsync(message, cancellationToken);
        if (http.StatusCode is HttpStatusCode.Conflict)
        {
            // The one typed error body in the API: the lease that is in the way.
            var conflict = await http.Content.ReadFromJsonAsync<DraftLeaseConflictResponse>(cancellationToken: cancellationToken);
            throw new DraftLeaseConflictClientException(
                conflict?.Error ?? "Draft is locked by another counter.",
                conflict?.ExistingLease ?? throw new InvalidOperationException("Conflict response missing existing lease."));
        }

        return await ApiResponseReader.ReadOrThrowAsync<DraftLeaseAcquireResult>(http, cancellationToken);
    }

    public Task<DraftLeaseResponse> RenewAsync(
        Guid leaseId,
        DraftLeaseRenewRequest request,
        CancellationToken cancellationToken = default)
        => httpClient.PostApiAsync<DraftLeaseResponse>($"/api/leases/drafts/{leaseId}/renew", request, cancellationToken);

    public Task<DraftLeaseResponse> ReleaseAsync(
        Guid leaseId,
        DraftLeaseReleaseRequest request,
        CancellationToken cancellationToken = default)
        => httpClient.PostApiAsync<DraftLeaseResponse>($"/api/leases/drafts/{leaseId}/release", request, cancellationToken);

    public Task<DraftLeaseResponse?> GetActiveForBillAsync(
        Guid billId,
        CancellationToken cancellationToken = default)
        => httpClient.GetApiOrNullAsync<DraftLeaseResponse>($"/api/leases/drafts/bill/{billId}", cancellationToken);

    public Task<DraftLeaseListResponse> ListActiveAsync(
        string adminToken,
        CancellationToken cancellationToken = default)
        => httpClient.GetApiAsync<DraftLeaseListResponse>("/api/leases/drafts/active", cancellationToken, adminToken);

    public Task<DraftLeaseResponse> ForceReleaseAsync(
        Guid leaseId,
        DraftLeaseForceReleaseRequest request,
        string adminToken,
        CancellationToken cancellationToken = default)
        => httpClient.PostApiAsync<DraftLeaseResponse>(
            $"/api/leases/drafts/{leaseId}/force-release", request, cancellationToken, adminToken);
}
