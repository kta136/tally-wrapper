namespace ShowroomBilling.Infrastructure.Persistence.Entities;

public sealed class InvoiceSequenceEntity
{
    public Guid ShowroomId { get; set; }

    public string FiscalYear { get; set; } = string.Empty;

    public string DocumentType { get; set; } = string.Empty;

    public long NextValue { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}
