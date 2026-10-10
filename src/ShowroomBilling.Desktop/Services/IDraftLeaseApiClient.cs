using ShowroomBilling.Contracts.Leases;

namespace ShowroomBilling.Desktop.Services;

public interface IDraftLeaseApiClient
{
    Task<DraftLeaseListResponse> ListActiveAsync(string adminToken, CancellationToken cancellationToken = default);

    Task<DraftLeaseResponse> ForceReleaseAsync(Guid leaseId, DraftLeaseForceReleaseRequest request, string adminToken, CancellationToken cancellationToken = default);
}
