using System.Net.Http;
using ShowroomBilling.Contracts.Masters;

namespace ShowroomBilling.Desktop.Services;

public sealed class MastersApiClient(HttpClient httpClient) : IMastersApiClient
{
    public Task<CompanySnapshotResponse> GetCompaniesAsync(CancellationToken cancellationToken = default)
        => httpClient.GetApiAsync<CompanySnapshotResponse>("/api/masters/companies", cancellationToken);

    public Task<LedgerSnapshotResponse> GetLedgersAsync(CancellationToken cancellationToken = default)
        => httpClient.GetApiAsync<LedgerSnapshotResponse>("/api/masters/ledgers", cancellationToken);

    public Task<VoucherTypeSnapshotResponse> GetVoucherTypesAsync(CancellationToken cancellationToken = default)
        => httpClient.GetApiAsync<VoucherTypeSnapshotResponse>("/api/masters/voucher-types", cancellationToken);

    public Task<StockItemSnapshotResponse> GetStockItemsAsync(CancellationToken cancellationToken = default)
        => httpClient.GetApiAsync<StockItemSnapshotResponse>("/api/masters/stock-items", cancellationToken);

    public Task<IReadOnlyList<TallyMasterRefreshResult>> RequestRefreshAsync(
        MasterRefreshRequest request,
        CancellationToken cancellationToken = default)
        => httpClient.PostApiAsync<IReadOnlyList<TallyMasterRefreshResult>>("/api/masters/refresh", request, cancellationToken);
}
