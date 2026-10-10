using System.Collections.ObjectModel;
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

/// <summary>
/// The three master sections of Settings. Each has a "fetch" (read the cached
/// snapshot into the dropdown) and a "refresh" (ask Tally again, then fetch).
/// </summary>
internal sealed class SettingsMasterSnapshotWorkflow(
    ISettingsApiClient? settingsApi,
    IMastersApiClient? mastersApi,
    ISettingsMasterSnapshotHost host,
    Func<CancellationToken, Task> reloadSettings)
{
    public Task FetchCompaniesAsync(CancellationToken cancellationToken) => FetchAsync(
        busy => host.IsFetchingCompanies = busy,
        "companies",
        async api =>
        {
            var response = await api.GetCompaniesAsync(cancellationToken: cancellationToken);
            var activeName = host.ActiveCompanyName;
            Replace(host.Companies, response.Companies);
            host.CompaniesCount = response.Metadata.ItemCount;
            host.CompaniesFreshness = response.Metadata.Freshness;
            host.CompaniesFetchedAtUtc = response.Metadata.FetchedAtUtc;
            host.SelectedCompany = host.Companies.FirstOrDefault(c => string.Equals(c.Name, activeName, StringComparison.Ordinal))
                ?? host.Companies.FirstOrDefault();

            return host.Companies.Count == 0
                ? "No companies cached — try Refresh from Tally."
                : $"{host.Companies.Count} compan{(host.Companies.Count == 1 ? "y" : "ies")} loaded · {response.Metadata.Freshness}";
        });

    public Task FetchLedgersAndVoucherTypesAsync(CancellationToken cancellationToken) => FetchAsync(
        busy => host.IsFetchingLedgers = busy,
        "ledgers & voucher types",
        async api =>
        {
            var ledgersTask = api.GetLedgersAsync(cancellationToken: cancellationToken);
            var voucherTypesTask = api.GetVoucherTypesAsync(cancellationToken: cancellationToken);
            await Task.WhenAll(ledgersTask, voucherTypesTask);

            var ledgers = await ledgersTask;
            Replace(host.LedgerOptions, ledgers.Ledgers);
            host.LedgersCount = ledgers.Metadata.ItemCount;
            host.LedgersFreshness = ledgers.Metadata.Freshness;
            host.LedgersFetchedAtUtc = ledgers.Metadata.FetchedAtUtc;

            var voucherTypes = await voucherTypesTask;
            Replace(host.VoucherTypeOptions, voucherTypes.VoucherTypes);
            host.VoucherTypesCount = voucherTypes.Metadata.ItemCount;

            return $"{host.LedgerOptions.Count} ledger(s) · {host.VoucherTypeOptions.Count} voucher type(s) · {ledgers.Metadata.Freshness}";
        });

    public Task FetchStockItemsAsync(CancellationToken cancellationToken) => FetchAsync(
        busy => host.IsFetchingStockItems = busy,
        "stock items",
        async api =>
        {
            var response = await api.GetStockItemsAsync(cancellationToken: cancellationToken);
            Replace(host.StockItems, response.StockItems);
            host.StockItemsCount = response.Metadata.ItemCount;
            host.StockItemsFreshness = response.Metadata.Freshness;
            host.StockItemsFetchedAtUtc = response.Metadata.FetchedAtUtc;

            return host.StockItems.Count == 0
                ? "No stock items cached — try Refresh from Tally."
                : $"{host.StockItems.Count} stock item(s) loaded · {response.Metadata.Freshness}";
        });

    public Task RequestCompanyRefreshAsync(CancellationToken cancellationToken) => RefreshAsync(
        busy => host.IsFetchingCompanies = busy,
        "companies",
        [MasterTypes.Companies],
        FetchCompaniesAsync,
        cancellationToken);

    public Task RequestLedgerRefreshAsync(CancellationToken cancellationToken) => RefreshAsync(
        busy => host.IsFetchingLedgers = busy,
        "ledgers + voucher types",
        [MasterTypes.Ledgers, MasterTypes.VoucherTypes],
        FetchLedgersAndVoucherTypesAsync,
        cancellationToken);

    public Task RequestStockItemRefreshAsync(CancellationToken cancellationToken) => RefreshAsync(
        busy => host.IsFetchingStockItems = busy,
        "stock items",
        [MasterTypes.StockItems],
        FetchStockItemsAsync,
        cancellationToken);

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
        catch (Exception ex)
        {
            host.StatusMessage = $"Set active failed: {ApiResponseReader.FormatError(ex)}";
        }
        finally
        {
            host.IsSettingActiveCompany = false;
        }
    }

    /// <summary>Reads a cached snapshot; <paramref name="load"/> returns the status text to show.</summary>
    private async Task FetchAsync(Action<bool> setBusy, string what, Func<IMastersApiClient, Task<string>> load)
    {
        if (mastersApi is null) return;

        setBusy(true);
        host.StatusMessage = $"Fetching {what}…";
        try
        {
            host.StatusMessage = await load(mastersApi);
        }
        catch (Exception ex)
        {
            host.StatusMessage = $"Fetch {what} failed: {ApiResponseReader.FormatError(ex)}";
        }
        finally
        {
            setBusy(false);
        }
    }

    /// <summary>
    /// Asks Tally for the named masters, then re-reads the snapshot. Each
    /// master is its own request, one after another: Tally's XML endpoint
    /// does not cope with concurrent reads, and asking for no master type at
    /// all would refresh all four.
    /// </summary>
    private async Task RefreshAsync(
        Action<bool> setBusy,
        string what,
        IReadOnlyList<string> masterTypes,
        Func<CancellationToken, Task> fetch,
        CancellationToken cancellationToken)
    {
        if (mastersApi is null) return;

        setBusy(true);
        host.StatusMessage = $"Refreshing {what} from Tally…";
        var refreshed = false;
        try
        {
            var results = new List<TallyMasterRefreshResult>();
            foreach (var masterType in masterTypes)
            {
                results.AddRange(await mastersApi.RequestRefreshAsync(
                    new MasterRefreshRequest(MasterType: masterType, RequestedByActor: "desktop-settings"),
                    cancellationToken));
            }

            refreshed = results.All(result => result.Succeeded);
            host.StatusMessage = SettingsRefreshSummaryFormatter.Summarize(results);
        }
        catch (Exception ex)
        {
            host.StatusMessage = $"Refresh failed: {ApiResponseReader.FormatError(ex)}";
        }
        finally
        {
            setBusy(false);
        }

        if (refreshed)
        {
            await fetch(cancellationToken);
        }
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }
}
