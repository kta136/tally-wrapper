namespace ShowroomBilling.Infrastructure.Persistence.Entities;

/// <summary>
/// No longer written or read: a bill's own row records its number, and a
/// repeated save is recognised by <c>bills.CreateOperationId</c>. The mapping
/// stays so the model matches the table (and the rows older builds wrote)
/// until a migration drops it.
/// </summary>
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
