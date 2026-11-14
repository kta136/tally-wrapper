using ShowroomBilling.Contracts.Masters;

namespace ShowroomBilling.Application.Masters;

/// <summary>
/// The lists last read from Tally. Storing one replaces the whole list;
/// reading one returns the whole list, in name order.
/// </summary>
public interface IMasterSnapshotService
{
    Task<MasterSnapshotMetadata> IngestCompaniesAsync(
        IReadOnlyList<CompanySnapshotItem> companies,
        CancellationToken cancellationToken = default);

    Task<MasterSnapshotMetadata> IngestLedgersAsync(
        IReadOnlyList<LedgerSnapshotItem> ledgers,
        CancellationToken cancellationToken = default);

    Task<MasterSnapshotMetadata> IngestStockItemsAsync(
        IReadOnlyList<StockItemSnapshotItem> stockItems,
        CancellationToken cancellationToken = default);

    Task<MasterSnapshotMetadata> IngestVoucherTypesAsync(
        IReadOnlyList<VoucherTypeSnapshotItem> voucherTypes,
        CancellationToken cancellationToken = default);

    Task<CompanySnapshotResponse> GetCompaniesAsync(CancellationToken cancellationToken = default);

    Task<LedgerSnapshotResponse> GetLedgersAsync(CancellationToken cancellationToken = default);

    Task<StockItemSnapshotResponse> GetStockItemsAsync(CancellationToken cancellationToken = default);

    Task<VoucherTypeSnapshotResponse> GetVoucherTypesAsync(CancellationToken cancellationToken = default);

    Task<MasterFreshnessSummaryResponse> GetFreshnessSummaryAsync(CancellationToken cancellationToken = default);
}
