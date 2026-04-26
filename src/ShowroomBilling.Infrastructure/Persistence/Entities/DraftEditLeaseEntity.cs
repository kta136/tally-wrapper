namespace ShowroomBilling.Infrastructure.Persistence.Entities;

public sealed class DraftEditLeaseEntity
{
    public Guid Id { get; set; }

    public Guid BillId { get; set; }

    public Guid ShowroomId { get; set; }

    public string OwnerActorId { get; set; } = string.Empty;

    public string? OwnerCounterName { get; set; }

    public DateTimeOffset AcquiredAtUtc { get; set; }

    public DateTimeOffset ExpiresAtUtc { get; set; }

    public DateTimeOffset RenewedAtUtc { get; set; }

    public DateTimeOffset? ReleasedAtUtc { get; set; }
}
