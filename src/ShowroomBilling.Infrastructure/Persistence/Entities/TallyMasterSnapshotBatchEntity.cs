namespace ShowroomBilling.Infrastructure.Persistence.Entities;

public sealed class TallyMasterSnapshotBatchEntity
{
    public Guid Id { get; set; }

    public Guid ShowroomId { get; set; }

    public string MasterType { get; set; } = string.Empty;

    public DateTimeOffset FetchedAtUtc { get; set; }

    public string Status { get; set; } = string.Empty;

    public int ItemCount { get; set; }

    public ICollection<TallyCompanySnapshotEntity> CompanySnapshots { get; set; } = [];

    public ICollection<TallyLedgerSnapshotEntity> LedgerSnapshots { get; set; } = [];

    public ICollection<TallyStockItemSnapshotEntity> StockItemSnapshots { get; set; } = [];

    public ICollection<TallyVoucherTypeSnapshotEntity> VoucherTypeSnapshots { get; set; } = [];
}
