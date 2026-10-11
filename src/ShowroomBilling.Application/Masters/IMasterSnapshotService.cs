using ShowroomBilling.Contracts.Masters;

namespace ShowroomBilling.Application.Masters;

public interface IMasterSnapshotService
{
    Task<MasterSnapshotAcceptedResponse> IngestCompaniesAsync(
        PushCompanySnapshotRequest request,
        CancellationToken cancellationToken = default);

    Task<MasterSnapshotAcceptedResponse> IngestLedgersAsync(
        PushLedgerSnapshotRequest request,
        CancellationToken cancellationToken = default);

    Task<MasterSnapshotAcceptedResponse> IngestStockItemsAsync(
        PushStockItemSnapshotRequest request,
        CancellationToken cancellationToken = default);

    Task<MasterSnapshotAcceptedResponse> IngestVoucherTypesAsync(
        PushVoucherTypeSnapshotRequest request,
        CancellationToken cancellationToken = default);

    Task<CompanySnapshotResponse> GetCompaniesAsync(CancellationToken cancellationToken = default);

    Task<LedgerSnapshotResponse> GetLedgersAsync(CancellationToken cancellationToken = default);

    Task<StockItemSnapshotResponse> GetStockItemsAsync(CancellationToken cancellationToken = default);

    Task<VoucherTypeSnapshotResponse> GetVoucherTypesAsync(CancellationToken cancellationToken = default);

    Task<MasterFreshnessSummaryResponse> GetFreshnessSummaryAsync(CancellationToken cancellationToken = default);
}
