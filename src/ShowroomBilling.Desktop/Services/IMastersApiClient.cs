using ShowroomBilling.Application.Tally;
using ShowroomBilling.Contracts.Masters;

namespace ShowroomBilling.Desktop.Services;

public interface IMastersApiClient
{
    Task<CompanySnapshotResponse> GetCompaniesAsync(
        CancellationToken cancellationToken = default,
        MasterSnapshotQuery? query = null);

    Task<LedgerSnapshotResponse> GetLedgersAsync(
        CancellationToken cancellationToken = default,
        MasterSnapshotQuery? query = null);

    Task<VoucherTypeSnapshotResponse> GetVoucherTypesAsync(
        CancellationToken cancellationToken = default,
        MasterSnapshotQuery? query = null);

    Task<StockItemSnapshotResponse> GetStockItemsAsync(
        CancellationToken cancellationToken = default,
        MasterSnapshotQuery? query = null);

    /// <summary>
    /// Calls <c>POST /api/masters/refresh</c>. The endpoint is synchronous: it
    /// fetches the requested master(s) from Tally and writes the snapshot
    /// before returning. The response is one <see cref="TallyMasterRefreshResult"/>
    /// per master fetched (a single-element list when <c>request.MasterType</c>
    /// is set, otherwise one entry per master type).
    /// </summary>
    Task<IReadOnlyList<TallyMasterRefreshResult>> RequestRefreshAsync(
        MasterRefreshRequest request,
        CancellationToken cancellationToken = default);
}
