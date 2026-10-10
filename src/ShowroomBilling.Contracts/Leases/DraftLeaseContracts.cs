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

public sealed record DraftLeaseListResponse(
    IReadOnlyList<DraftLeaseResponse> Leases);

public sealed record DraftLeaseForceReleaseRequest(
    string AdminActorLabel,
    string Reason);
