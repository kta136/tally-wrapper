namespace ShowroomBilling.Infrastructure.Persistence.Entities;

public sealed class BillEntity
{
    public Guid Id { get; set; }

    public Guid ShowroomId { get; set; }

    public Guid? CounterId { get; set; }

    public string BillType { get; set; } = "sales";

    public Guid? CurrentRevisionId { get; set; }

    public string State { get; set; } = "pending";

    public string? InvoiceNumber { get; set; }

    public string? FiscalYear { get; set; }

    public Guid? SupersededByBillId { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public DateTimeOffset? VoidedAtUtc { get; set; }

    public bool EditedAfterPush { get; set; }

    public ICollection<BillRevisionEntity> Revisions { get; set; } = new List<BillRevisionEntity>();
}
