namespace ShowroomBilling.Infrastructure.Persistence.Entities;

public sealed class BillRevisionEntity
{
    public Guid Id { get; set; }

    public Guid BillId { get; set; }

    public int RevisionNo { get; set; }

    public string SnapshotJson { get; set; } = "{}";

    public string TotalsJson { get; set; } = "{}";

    public string? PartyName { get; set; }

    public DateOnly? BillDate { get; set; }

    public decimal GrandTotal { get; set; }

    public Guid? SupersedesRevisionId { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset? SubmittedAtUtc { get; set; }

    public DateTimeOffset? FinalizedAtUtc { get; set; }

    public BillEntity? Bill { get; set; }
}
