using System.Collections.ObjectModel;
using System.Net.Http;
using ShowroomBilling.Contracts.Masters;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Desktop.Services;

namespace ShowroomBilling.Desktop.ViewModels.Settings;

internal interface ISettingsMasterSnapshotHost
{
    string StatusMessage { get; set; }
    string ActiveCompanyName { get; }
    CompanySnapshotItem? SelectedCompany { get; set; }
    bool IsFetchingCompanies { get; set; }
    bool IsSettingActiveCompany { get; set; }
    string CompaniesFreshness { get; set; }
    DateTimeOffset? CompaniesFetchedAtUtc { get; set; }
    int CompaniesCount { get; set; }
    bool IsFetchingLedgers { get; set; }
    string LedgersFreshness { get; set; }
    DateTimeOffset? LedgersFetchedAtUtc { get; set; }
    int LedgersCount { get; set; }
    int VoucherTypesCount { get; set; }
    bool IsFetchingStockItems { get; set; }
    string StockItemsFreshness { get; set; }
    DateTimeOffset? StockItemsFetchedAtUtc { get; set; }
    int StockItemsCount { get; set; }
    string SettingsSource { get; set; }
    string Summary { get; set; }
    DateTimeOffset? UpdatedAtUtc { get; set; }
    ObservableCollection<CompanySnapshotItem> Companies { get; }
    ObservableCollection<LedgerSnapshotItem> LedgerOptions { get; }
    ObservableCollection<VoucherTypeSnapshotItem> VoucherTypeOptions { get; }
    ObservableCollection<StockItemSnapshotItem> StockItems { get; }
}

internal sealed class SettingsMasterSnapshotWorkflow(
    ISettingsApiClient? settingsApi,
    IMastersApiClient? mastersApi,
    ISettingsMasterSnapshotHost host,
    Func<CancellationToken, Task> reloadSettings)
{
    public async Task FetchCompaniesAsync(CancellationToken cancellationToken)
    {
        if (mastersApi is null) return;

        host.IsFetchingCompanies = true;
        host.StatusMessage = "Fetching companies…";
        try
        {
            var response = await mastersApi.GetCompaniesAsync(cancellationToken);
            var activeName = host.ActiveCompanyName;
            host.Companies.Clear();
            foreach (var company in response.Companies)
                host.Companies.Add(company);

            host.CompaniesCount = response.Metadata.ItemCount;
            host.CompaniesFreshness = response.Metadata.Freshness;
            host.CompaniesFetchedAtUtc = response.Metadata.FetchedAtUtc;
            host.SelectedCompany = host.Companies.FirstOrDefault(c => string.Equals(c.Name, activeName, StringComparison.Ordinal))
                ?? host.Companies.FirstOrDefault();

            host.StatusMessage = host.Companies.Count == 0
                ? "No companies cached — try Refresh from Tally."
                : $"{host.Companies.Count} compan{(host.Companies.Count == 1 ? "y" : "ies")} loaded · {response.Metadata.Freshness}";
        }
        catch (HttpRequestException ex)
        {
            host.StatusMessage = $"Fetch companies failed: {ApiResponseReader.FormatError(ex)}";
        }
        catch (Exception ex)
        {
            host.StatusMessage = $"Fetch companies failed: {ex.Message}";
        }
        finally
        {
            host.IsFetchingCompanies = false;
        }
    }

    public async Task RequestCompanyRefreshAsync(CancellationToken cancellationToken)
    {
        if (mastersApi is null) return;

        host.IsFetchingCompanies = true;
        host.StatusMessage = "Refreshing companies from Tally…";
        var refreshOk = false;
        try
        {
            var response = await mastersApi.RequestRefreshAsync(
                new MasterRefreshRequest(MasterType: "companies", RequestedByActor: "desktop-settings"),
                cancellationToken);
            refreshOk = response.All(r => r.Succeeded);
            host.StatusMessage = SettingsRefreshSummaryFormatter.Summarize(response);
        }
        catch (HttpRequestException ex)
        {
            host.StatusMessage = $"Refresh failed: {ApiResponseReader.FormatError(ex)}";
        }
        catch (Exception ex)
        {
            host.StatusMessage = $"Refresh failed: {ex.Message}";
        }
        finally
        {
            host.IsFetchingCompanies = false;
        }

        if (refreshOk)
        {
            await FetchCompaniesAsync(cancellationToken);
        }
    }

    public async Task SetActiveCompanyAsync(CancellationToken cancellationToken)
    {
        if (settingsApi is null || host.SelectedCompany is null) return;

        host.IsSettingActiveCompany = true;
        var targetName = host.SelectedCompany.Name;
        host.StatusMessage = $"Setting active company to {targetName}…";
        try
        {
            var response = await settingsApi.SelectActiveCompanyAsync(
                new SelectActiveCompanyRequest(targetName), cancellationToken);
            host.SettingsSource = response.SettingsSource;
            host.Summary = response.Summary;
            host.UpdatedAtUtc = response.UpdatedAtUtc;
            await reloadSettings(cancellationToken);
            host.StatusMessage = $"Active company set to {targetName}.";
        }
        catch (HttpRequestException ex)
        {
            host.StatusMessage = $"Set active failed: {ApiResponseReader.FormatError(ex)}";
        }
        catch (Exception ex)
        {
            host.StatusMessage = $"Set active failed: {ex.Message}";
        }
        finally
        {
            host.IsSettingActiveCompany = false;
        }
    }

    public async Task FetchLedgersAndVoucherTypesAsync(CancellationToken cancellationToken)
    {
        if (mastersApi is null) return;

        host.IsFetchingLedgers = true;
        host.StatusMessage = "Fetching ledgers & voucher types…";
        try
        {
            var ledgersTask = mastersApi.GetLedgersAsync(cancellationToken);
            var voucherTypesTask = mastersApi.GetVoucherTypesAsync(cancellationToken);
            await Task.WhenAll(ledgersTask, voucherTypesTask);

            var ledgersResponse = await ledgersTask;
            host.LedgerOptions.Clear();
            foreach (var ledger in ledgersResponse.Ledgers)
                host.LedgerOptions.Add(ledger);
            host.LedgersCount = ledgersResponse.Metadata.ItemCount;
            host.LedgersFreshness = ledgersResponse.Metadata.Freshness;
            host.LedgersFetchedAtUtc = ledgersResponse.Metadata.FetchedAtUtc;

            var voucherTypesResponse = await voucherTypesTask;
            host.VoucherTypeOptions.Clear();
            foreach (var voucherType in voucherTypesResponse.VoucherTypes)
                host.VoucherTypeOptions.Add(voucherType);
            host.VoucherTypesCount = voucherTypesResponse.Metadata.ItemCount;

            host.StatusMessage = $"{host.LedgerOptions.Count} ledger(s) · {host.VoucherTypeOptions.Count} voucher type(s) · {ledgersResponse.Metadata.Freshness}";
        }
        catch (HttpRequestException ex)
        {
            host.StatusMessage = $"Fetch ledgers failed: {ApiResponseReader.FormatError(ex)}";
        }
        catch (Exception ex)
        {
            host.StatusMessage = $"Fetch ledgers failed: {ex.Message}";
        }
        finally
        {
            host.IsFetchingLedgers = false;
        }
    }

    public async Task RequestLedgerRefreshAsync(CancellationToken cancellationToken)
    {
        if (mastersApi is null) return;

        host.IsFetchingLedgers = true;
        host.StatusMessage = "Refreshing ledgers + voucher types from Tally…";
        var refreshOk = false;
        try
        {
            var response = await mastersApi.RequestRefreshAsync(
                new MasterRefreshRequest(MasterType: null, RequestedByActor: "desktop-settings"),
                cancellationToken);
            refreshOk = response.All(r => r.Succeeded);
            host.StatusMessage = SettingsRefreshSummaryFormatter.Summarize(response);
        }
        catch (HttpRequestException ex)
        {
            host.StatusMessage = $"Refresh failed: {ApiResponseReader.FormatError(ex)}";
        }
        catch (Exception ex)
        {
            host.StatusMessage = $"Refresh failed: {ex.Message}";
        }
        finally
        {
            host.IsFetchingLedgers = false;
        }

        if (refreshOk)
        {
            await FetchLedgersAndVoucherTypesAsync(cancellationToken);
        }
    }

    public async Task FetchStockItemsAsync(CancellationToken cancellationToken)
    {
        if (mastersApi is null) return;

        host.IsFetchingStockItems = true;
        host.StatusMessage = "Fetching stock items…";
        try
        {
            var response = await mastersApi.GetStockItemsAsync(cancellationToken);
            host.StockItems.Clear();
            foreach (var item in response.StockItems)
                host.StockItems.Add(item);

            host.StockItemsCount = response.Metadata.ItemCount;
            host.StockItemsFreshness = response.Metadata.Freshness;
            host.StockItemsFetchedAtUtc = response.Metadata.FetchedAtUtc;

            host.StatusMessage = host.StockItems.Count == 0
                ? "No stock items cached — try Refresh from Tally."
                : $"{host.StockItems.Count} stock item(s) loaded · {response.Metadata.Freshness}";
        }
        catch (HttpRequestException ex)
        {
            host.StatusMessage = $"Fetch stock items failed: {ApiResponseReader.FormatError(ex)}";
        }
        catch (Exception ex)
        {
            host.StatusMessage = $"Fetch stock items failed: {ex.Message}";
        }
        finally
        {
            host.IsFetchingStockItems = false;
        }
    }

    public async Task RequestStockItemRefreshAsync(CancellationToken cancellationToken)
    {
        if (mastersApi is null) return;

        host.IsFetchingStockItems = true;
        host.StatusMessage = "Refreshing stock items from Tally…";
        var refreshOk = false;
        try
        {
            var response = await mastersApi.RequestRefreshAsync(
                new MasterRefreshRequest(MasterType: "stock-items", RequestedByActor: "desktop-settings"),
                cancellationToken);
            refreshOk = response.All(r => r.Succeeded);
            host.StatusMessage = SettingsRefreshSummaryFormatter.Summarize(response);
        }
        catch (HttpRequestException ex)
        {
            host.StatusMessage = $"Refresh failed: {ApiResponseReader.FormatError(ex)}";
        }
        catch (Exception ex)
        {
            host.StatusMessage = $"Refresh failed: {ex.Message}";
        }
        finally
        {
            host.IsFetchingStockItems = false;
        }

        if (refreshOk)
        {
            await FetchStockItemsAsync(cancellationToken);
        }
    }
}
