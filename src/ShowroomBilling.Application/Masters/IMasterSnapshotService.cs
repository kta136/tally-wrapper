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
        MasterSnapshotQuery? query = null,
        CancellationToken cancellationToken = default);

    Task<LedgerSnapshotResponse> GetLedgersAsync(
        MasterSnapshotQuery? query = null,
        CancellationToken cancellationToken = default);

    Task<StockItemSnapshotResponse> GetStockItemsAsync(
        MasterSnapshotQuery? query = null,
        CancellationToken cancellationToken = default);

    Task<VoucherTypeSnapshotResponse> GetVoucherTypesAsync(
        MasterSnapshotQuery? query = null,
        CancellationToken cancellationToken = default);

    Task<MasterFreshnessSummaryResponse> GetFreshnessSummaryAsync(CancellationToken cancellationToken = default);
}
