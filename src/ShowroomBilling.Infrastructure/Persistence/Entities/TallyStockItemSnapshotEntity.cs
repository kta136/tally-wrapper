namespace ShowroomBilling.Infrastructure.Persistence.Entities;

public sealed class TallyStockItemSnapshotEntity
{
    public Guid Id { get; set; }

    public Guid BatchId { get; set; }

    public Guid ShowroomId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Alias { get; set; }

    public string? BaseUnit { get; set; }

    public string? HsnCode { get; set; }

    public string? Karat { get; set; }

    public string RawJson { get; set; } = "{}";

    public TallyMasterSnapshotBatchEntity Batch { get; set; } = null!;
}
