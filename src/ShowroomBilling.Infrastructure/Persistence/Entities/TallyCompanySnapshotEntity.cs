namespace ShowroomBilling.Infrastructure.Persistence.Entities;

public sealed class TallyCompanySnapshotEntity
{
    public Guid Id { get; set; }

    public Guid BatchId { get; set; }

    public Guid ShowroomId { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool IsActive { get; set; }

    public string RawJson { get; set; } = "{}";

    public TallyMasterSnapshotBatchEntity Batch { get; set; } = null!;
}
