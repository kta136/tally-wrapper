using ShowroomBilling.Contracts.Leases;

namespace ShowroomBilling.Application.Leases;

/// <summary>
/// The admin side of draft edit leases: list and force-release. Nothing takes
/// a lease; acquire, renew and release were removed because no client ever
/// called them.
/// </summary>
public interface IDraftLeaseService
{
    Task<DraftLeaseListResponse> ListActiveAsync(CancellationToken cancellationToken = default);

    Task<DraftLeaseResponse> ForceReleaseAsync(
        Guid leaseId,
        DraftLeaseForceReleaseRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class DraftLeaseNotFoundException(Guid leaseId)
    : Exception($"Draft lease '{leaseId}' was not found or already released.");
