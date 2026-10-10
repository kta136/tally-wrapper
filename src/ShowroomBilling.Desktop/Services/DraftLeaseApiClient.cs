using System.Net.Http;
using ShowroomBilling.Contracts.Leases;

namespace ShowroomBilling.Desktop.Services;

public sealed class DraftLeaseApiClient(HttpClient httpClient) : IDraftLeaseApiClient
{
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
