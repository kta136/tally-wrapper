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

    Task<CompanySnapshotResponse> GetCompaniesAsync(
        CancellationToken cancellationToken = default,
        MasterSnapshotQuery? query = null);

    Task<LedgerSnapshotResponse> GetLedgersAsync(
        CancellationToken cancellationToken = default,
        MasterSnapshotQuery? query = null);

    Task<StockItemSnapshotResponse> GetStockItemsAsync(
        CancellationToken cancellationToken = default,
        MasterSnapshotQuery? query = null);

    Task<VoucherTypeSnapshotResponse> GetVoucherTypesAsync(
        CancellationToken cancellationToken = default,
        MasterSnapshotQuery? query = null);

    Task<MasterFreshnessSummaryResponse> GetFreshnessSummaryAsync(CancellationToken cancellationToken = default);
}
