namespace ShowroomBilling.Contracts.Masters;

// What is kept of each master read from Tally. These are also the shape the
// lists are stored in (tally_masters."ItemsJson"), so a property renamed here
// needs the stored lists refreshed from Tally.

public sealed record CompanySnapshotItem(
    string Name,
    bool IsActive);

/// <summary>A ledger of the Tally company. Only its name is kept: nothing ever read its group, revenue flag or GSTIN.</summary>
public sealed record LedgerSnapshotItem(string Name);

public sealed record StockItemSnapshotItem(
    string Name,
    string? BaseUnit,
    string? HsnCode);

/// <summary>A voucher type of the Tally company. Only its name is kept.</summary>
public sealed record VoucherTypeSnapshotItem(string Name);
