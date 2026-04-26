namespace ShowroomBilling.Infrastructure.Persistence.Entities;

public sealed class InvoiceNumberReservationEntity
{
    public Guid Id { get; set; }

    public string IdempotencyKey { get; set; } = string.Empty;

    public Guid ShowroomId { get; set; }

    public string FiscalYear { get; set; } = string.Empty;

    public string DocumentType { get; set; } = string.Empty;

    public long ReservedValue { get; set; }

    public string FormattedNumber { get; set; } = string.Empty;

    public string? ReservedForReference { get; set; }

    public DateTimeOffset ReservedAtUtc { get; set; }
}
