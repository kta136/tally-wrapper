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

    /// <summary>
    /// The numeric sequence core behind <see cref="InvoiceNumber"/>, stored
    /// explicitly so occupancy and rollback never have to parse display text
    /// (a suffix such as <c>-A2</c> made trailing-digit parsing wrong).
    /// Null only for legacy custom numbers that have no core.
    /// </summary>
    public long? InvoiceCore { get; set; }

    public Guid? SupersededByBillId { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public DateTimeOffset? VoidedAtUtc { get; set; }

    public bool EditedAfterPush { get; set; }

    /// <summary>
    /// Optimistic concurrency token, mapped to PostgreSQL's <c>xmin</c> system
    /// column. Every tracked save of a bill checks it, so a stale reader cannot
    /// overwrite a transition another request committed in between.
    /// </summary>
    public uint RowVersion { get; set; }

    public ICollection<BillRevisionEntity> Revisions { get; set; } = new List<BillRevisionEntity>();
}
