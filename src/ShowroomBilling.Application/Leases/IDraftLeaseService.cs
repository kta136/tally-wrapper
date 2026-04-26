using ShowroomBilling.Contracts.Leases;

namespace ShowroomBilling.Application.Leases;

public interface IDraftLeaseService
{
    Task<DraftLeaseAcquireResult> AcquireAsync(
        DraftLeaseAcquireRequest request,
        CancellationToken cancellationToken = default);

    Task<DraftLeaseResponse> RenewAsync(
        Guid leaseId,
        DraftLeaseRenewRequest request,
        CancellationToken cancellationToken = default);

    Task<DraftLeaseResponse> ReleaseAsync(
        Guid leaseId,
        DraftLeaseReleaseRequest request,
        CancellationToken cancellationToken = default);

    Task<DraftLeaseResponse?> GetActiveForBillAsync(
        Guid billId,
        CancellationToken cancellationToken = default);

    Task<DraftLeaseListResponse> ListActiveAsync(CancellationToken cancellationToken = default);

    Task<DraftLeaseResponse> ForceReleaseAsync(
        Guid leaseId,
        DraftLeaseForceReleaseRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class DraftLeaseConflictException(string message, Contracts.Leases.DraftLeaseResponse existingLease)
    : InvalidOperationException(message)
{
    public Contracts.Leases.DraftLeaseResponse ExistingLease { get; } = existingLease;
}

public sealed class DraftLeaseNotFoundException(Guid leaseId)
    : Exception($"Draft lease '{leaseId}' was not found or already released.");

public sealed class DraftLeaseOwnershipException(string message)
    : InvalidOperationException(message);
