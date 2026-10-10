using System.Net.Http;
using ShowroomBilling.Application.Tally;
using ShowroomBilling.Contracts.Masters;

namespace ShowroomBilling.Desktop.Services;

public sealed class MastersApiClient(HttpClient httpClient) : IMastersApiClient
{
    public Task<CompanySnapshotResponse> GetCompaniesAsync(
        MasterSnapshotQuery? query = null,
        CancellationToken cancellationToken = default)
        => httpClient.GetApiAsync<CompanySnapshotResponse>(BuildUrl("/api/masters/companies", query), cancellationToken);

    public Task<LedgerSnapshotResponse> GetLedgersAsync(
        MasterSnapshotQuery? query = null,
        CancellationToken cancellationToken = default)
        => httpClient.GetApiAsync<LedgerSnapshotResponse>(BuildUrl("/api/masters/ledgers", query), cancellationToken);

    public Task<VoucherTypeSnapshotResponse> GetVoucherTypesAsync(
        MasterSnapshotQuery? query = null,
        CancellationToken cancellationToken = default)
        => httpClient.GetApiAsync<VoucherTypeSnapshotResponse>(BuildUrl("/api/masters/voucher-types", query), cancellationToken);

    public Task<StockItemSnapshotResponse> GetStockItemsAsync(
        MasterSnapshotQuery? query = null,
        CancellationToken cancellationToken = default)
        => httpClient.GetApiAsync<StockItemSnapshotResponse>(BuildUrl("/api/masters/stock-items", query), cancellationToken);

    public Task<IReadOnlyList<TallyMasterRefreshResult>> RequestRefreshAsync(
        MasterRefreshRequest request,
        CancellationToken cancellationToken = default)
        => httpClient.PostApiAsync<IReadOnlyList<TallyMasterRefreshResult>>("/api/masters/refresh", request, cancellationToken);

    private static string BuildUrl(string path, MasterSnapshotQuery? query)
    {
        if (query is null)
        {
            return path;
        }

        var qp = new List<string>();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            qp.Add($"search={Uri.EscapeDataString(query.Search)}");
        }
        if (query.Skip is not null)
        {
            qp.Add($"skip={query.Skip.Value}");
        }
        if (query.Take is not null)
        {
            qp.Add($"take={query.Take.Value}");
        }
        if (query.IncludeRaw is true)
        {
            qp.Add("includeRaw=true");
        }

        return qp.Count == 0 ? path : $"{path}?{string.Join('&', qp)}";
    }
}
