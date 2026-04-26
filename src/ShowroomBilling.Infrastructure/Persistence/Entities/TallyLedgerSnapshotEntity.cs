namespace ShowroomBilling.Infrastructure.Persistence.Entities;

public sealed class TallyLedgerSnapshotEntity
{
    public Guid Id { get; set; }

    public Guid BatchId { get; set; }

    public Guid ShowroomId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Parent { get; set; }

    public string? PrimaryGroup { get; set; }

    public bool IsRevenue { get; set; }

    public string? Gstin { get; set; }

    public string RawJson { get; set; } = "{}";

    public TallyMasterSnapshotBatchEntity Batch { get; set; } = null!;
}
