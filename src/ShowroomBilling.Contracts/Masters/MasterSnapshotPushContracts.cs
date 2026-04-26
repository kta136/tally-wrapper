namespace ShowroomBilling.Contracts.Masters;

public sealed record PushCompanySnapshotRequest(
    string ShowroomCode,
    DateTimeOffset FetchedAtUtc,
    IReadOnlyList<CompanySnapshotItem> Companies);

public sealed record CompanySnapshotItem(
    string Name,
    bool IsActive,
    string? RawJson);

public sealed record PushLedgerSnapshotRequest(
    string ShowroomCode,
    DateTimeOffset FetchedAtUtc,
    IReadOnlyList<LedgerSnapshotItem> Ledgers);

public sealed record LedgerSnapshotItem(
    string Name,
    string? Parent,
    string? PrimaryGroup,
    bool IsRevenue,
    string? Gstin,
    string? RawJson);

public sealed record PushStockItemSnapshotRequest(
    string ShowroomCode,
    DateTimeOffset FetchedAtUtc,
    IReadOnlyList<StockItemSnapshotItem> StockItems);

public sealed record StockItemSnapshotItem(
    string Name,
    string? Alias,
    string? BaseUnit,
    string? HsnCode,
    string? Karat,
    string? RawJson);

public sealed record PushVoucherTypeSnapshotRequest(
    string ShowroomCode,
    DateTimeOffset FetchedAtUtc,
    IReadOnlyList<VoucherTypeSnapshotItem> VoucherTypes);

public sealed record VoucherTypeSnapshotItem(
    string Name,
    string? ParentType,
    bool IsDeemedPositive,
    string? RawJson);

public sealed record MasterSnapshotAcceptedResponse(
    string MasterType,
    string BatchId,
    int ItemCount,
    DateTimeOffset FetchedAtUtc,
    DateTimeOffset StoredAtUtc,
    string Message);
