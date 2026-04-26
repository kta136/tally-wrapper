using System.Net.Http;
using System.Net.Http.Json;
using ShowroomBilling.Application.Tally;
using ShowroomBilling.Contracts.Masters;

namespace ShowroomBilling.Desktop.Services;

public sealed class MastersApiClient(HttpClient httpClient) : IMastersApiClient
{
    public async Task<CompanySnapshotResponse> GetCompaniesAsync(
        CancellationToken cancellationToken = default,
        MasterSnapshotQuery? query = null)
    {
        var response = await httpClient.GetFromJsonAsync<CompanySnapshotResponse>(
            BuildUrl("/api/masters/companies", query),
            cancellationToken);
        return response ?? throw new InvalidOperationException("API returned an empty companies payload.");
    }

    public async Task<LedgerSnapshotResponse> GetLedgersAsync(
        CancellationToken cancellationToken = default,
        MasterSnapshotQuery? query = null)
    {
        var response = await httpClient.GetFromJsonAsync<LedgerSnapshotResponse>(
            BuildUrl("/api/masters/ledgers", query),
            cancellationToken);
        return response ?? throw new InvalidOperationException("API returned an empty ledgers payload.");
    }

    public async Task<VoucherTypeSnapshotResponse> GetVoucherTypesAsync(
        CancellationToken cancellationToken = default,
        MasterSnapshotQuery? query = null)
    {
        var response = await httpClient.GetFromJsonAsync<VoucherTypeSnapshotResponse>(
            BuildUrl("/api/masters/voucher-types", query),
            cancellationToken);
        return response ?? throw new InvalidOperationException("API returned an empty voucher-types payload.");
    }

    public async Task<StockItemSnapshotResponse> GetStockItemsAsync(
        CancellationToken cancellationToken = default,
        MasterSnapshotQuery? query = null)
    {
        var response = await httpClient.GetFromJsonAsync<StockItemSnapshotResponse>(
            BuildUrl("/api/masters/stock-items", query),
            cancellationToken);
        return response ?? throw new InvalidOperationException("API returned an empty stock-items payload.");
    }

    public async Task<IReadOnlyList<TallyMasterRefreshResult>> RequestRefreshAsync(
        MasterRefreshRequest request,
        CancellationToken cancellationToken = default)
    {
        var http = await httpClient.PostAsJsonAsync("/api/masters/refresh", request, cancellationToken);
        return await ApiResponseReader.ReadOrThrowAsync<IReadOnlyList<TallyMasterRefreshResult>>(http, cancellationToken);
    }

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
