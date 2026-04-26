namespace ShowroomBilling.Contracts.Leases;

public sealed record DraftLeaseResponse(
    Guid Id,
    Guid BillId,
    Guid ShowroomId,
    string OwnerActorId,
    string? OwnerCounterName,
    DateTimeOffset AcquiredAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset RenewedAtUtc,
    DateTimeOffset? ReleasedAtUtc);

public sealed record DraftLeaseAcquireRequest(
    Guid BillId,
    string OwnerActorId,
    string? OwnerCounterName);

public sealed record DraftLeaseAcquireResult(
    DraftLeaseResponse Lease,
    bool Reacquired);

public sealed record DraftLeaseListResponse(
    IReadOnlyList<DraftLeaseResponse> Leases);

public sealed record DraftLeaseReleaseRequest(
    string OwnerActorId);

public sealed record DraftLeaseRenewRequest(
    string OwnerActorId);

public sealed record DraftLeaseForceReleaseRequest(
    string AdminActorLabel,
    string Reason);

public sealed record DraftLeaseConflictResponse(
    string Error,
    DraftLeaseResponse ExistingLease);
