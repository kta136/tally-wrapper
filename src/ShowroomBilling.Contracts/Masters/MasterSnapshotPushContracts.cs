namespace ShowroomBilling.Contracts.Masters;

public sealed record PushCompanySnapshotRequest(
    string ShowroomCode,
    DateTimeOffset FetchedAtUtc,
    IReadOnlyList<CompanySnapshotItem> Companies);

public sealed record CompanySnapshotItem(
    string Name,
    bool IsActive);

public sealed record PushLedgerSnapshotRequest(
    string ShowroomCode,
    DateTimeOffset FetchedAtUtc,
    IReadOnlyList<LedgerSnapshotItem> Ledgers);

/// <summary>A ledger of the Tally company. Only its name is kept: nothing ever read its group, revenue flag or GSTIN.</summary>
public sealed record LedgerSnapshotItem(string Name);

public sealed record PushStockItemSnapshotRequest(
    string ShowroomCode,
    DateTimeOffset FetchedAtUtc,
    IReadOnlyList<StockItemSnapshotItem> StockItems);

public sealed record StockItemSnapshotItem(
    string Name,
    string? Alias,
    string? BaseUnit,
    string? HsnCode,
    string? Karat);

public sealed record PushVoucherTypeSnapshotRequest(
    string ShowroomCode,
    DateTimeOffset FetchedAtUtc,
    IReadOnlyList<VoucherTypeSnapshotItem> VoucherTypes);

/// <summary>A voucher type of the Tally company. Only its name is kept.</summary>
public sealed record VoucherTypeSnapshotItem(string Name);

public sealed record MasterSnapshotAcceptedResponse(
    string MasterType,
    string BatchId,
    int ItemCount,
    DateTimeOffset FetchedAtUtc,
    DateTimeOffset StoredAtUtc,
    string Message);
