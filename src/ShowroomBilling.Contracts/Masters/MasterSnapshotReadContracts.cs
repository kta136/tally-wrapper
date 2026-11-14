namespace ShowroomBilling.Contracts.Masters;

/// <summary>When a master list was last read from Tally and how many entries it holds.</summary>
public sealed record MasterSnapshotMetadata(
    string MasterType,
    DateTimeOffset? FetchedAtUtc,
    int ItemCount,
    string Freshness);

public sealed record CompanySnapshotResponse(
    MasterSnapshotMetadata Metadata,
    IReadOnlyList<CompanySnapshotItem> Companies);

public sealed record LedgerSnapshotResponse(
    MasterSnapshotMetadata Metadata,
    IReadOnlyList<LedgerSnapshotItem> Ledgers);

public sealed record StockItemSnapshotResponse(
    MasterSnapshotMetadata Metadata,
    IReadOnlyList<StockItemSnapshotItem> StockItems);

public sealed record VoucherTypeSnapshotResponse(
    MasterSnapshotMetadata Metadata,
    IReadOnlyList<VoucherTypeSnapshotItem> VoucherTypes);

public sealed record MasterFreshnessSummaryResponse(
    string OverallStatus,
    IReadOnlyList<MasterSnapshotMetadata> Masters);

public sealed record MasterRefreshRequest(string? MasterType);
