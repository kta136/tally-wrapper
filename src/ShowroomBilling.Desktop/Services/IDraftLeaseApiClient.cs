using ShowroomBilling.Contracts.Leases;

namespace ShowroomBilling.Desktop.Services;

public interface IDraftLeaseApiClient
{
    Task<DraftLeaseAcquireResult> AcquireAsync(DraftLeaseAcquireRequest request, CancellationToken cancellationToken = default);

    Task<DraftLeaseResponse> RenewAsync(Guid leaseId, DraftLeaseRenewRequest request, CancellationToken cancellationToken = default);

    Task<DraftLeaseResponse> ReleaseAsync(Guid leaseId, DraftLeaseReleaseRequest request, CancellationToken cancellationToken = default);

    Task<DraftLeaseResponse?> GetActiveForBillAsync(Guid billId, CancellationToken cancellationToken = default);

    Task<DraftLeaseListResponse> ListActiveAsync(string adminToken, CancellationToken cancellationToken = default);

    Task<DraftLeaseResponse> ForceReleaseAsync(Guid leaseId, DraftLeaseForceReleaseRequest request, string adminToken, CancellationToken cancellationToken = default);
}

public sealed class DraftLeaseConflictClientException(string message, DraftLeaseResponse existingLease) : Exception(message)
{
    public DraftLeaseResponse ExistingLease { get; } = existingLease;
}
