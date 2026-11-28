using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using ShowroomBilling.Contracts.Masters;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Desktop.Services;

namespace ShowroomBilling.Desktop.ViewModels.Settings;

public partial class MasterDataSettingsViewModel
{
    private Task FetchCompaniesAsync(bool announce, CancellationToken cancellationToken) => FetchAsync(
        announce,
        busy => IsFetchingCompanies = busy,
        "companies",
        async () =>
        {
            var response = await _mastersApi.GetCompaniesAsync(cancellationToken: cancellationToken);
            var activeName = ActiveCompanyName;
            Replace(Companies, response.Companies);
            CompaniesCount = response.Metadata.ItemCount;
            CompaniesFreshness = response.Metadata.Freshness;
            CompaniesFetchedAtUtc = response.Metadata.FetchedAtUtc;
            SelectedCompany = Companies.FirstOrDefault(c => string.Equals(c.Name, activeName, StringComparison.Ordinal))
                ?? Companies.FirstOrDefault();

            return Companies.Count == 0
                ? "No companies cached — try Refresh from Tally."
                : $"{Companies.Count} compan{(Companies.Count == 1 ? "y" : "ies")} loaded · {response.Metadata.Freshness}";
        });

    private Task FetchLedgersAndVoucherTypesAsync(bool announce, CancellationToken cancellationToken) => FetchAsync(
        announce,
        busy => IsFetchingLedgers = busy,
        "ledgers & voucher types",
        async () =>
        {
            var ledgersTask = _mastersApi.GetLedgersAsync(cancellationToken: cancellationToken);
            var voucherTypesTask = _mastersApi.GetVoucherTypesAsync(cancellationToken: cancellationToken);
            await Task.WhenAll(ledgersTask, voucherTypesTask);

            var ledgers = await ledgersTask;
            Replace(LedgerOptions, ledgers.Ledgers);
            LedgersCount = ledgers.Metadata.ItemCount;
            LedgersFreshness = ledgers.Metadata.Freshness;
            LedgersFetchedAtUtc = ledgers.Metadata.FetchedAtUtc;

            var voucherTypes = await voucherTypesTask;
            Replace(VoucherTypeOptions, voucherTypes.VoucherTypes);
            VoucherTypesCount = voucherTypes.Metadata.ItemCount;

            return $"{LedgerOptions.Count} ledger(s) · {VoucherTypeOptions.Count} voucher type(s) · {ledgers.Metadata.Freshness}";
        });

    private Task FetchStockItemsAsync(bool announce, CancellationToken cancellationToken) => FetchAsync(
        announce,
        busy => IsFetchingStockItems = busy,
        "stock items",
        async () =>
        {
            var response = await _mastersApi.GetStockItemsAsync(cancellationToken: cancellationToken);
            Replace(StockItems, response.StockItems);
            StockItemsCount = response.Metadata.ItemCount;
            StockItemsFreshness = response.Metadata.Freshness;
            StockItemsFetchedAtUtc = response.Metadata.FetchedAtUtc;

            return StockItems.Count == 0
                ? "No stock items cached — try Refresh from Tally."
                : $"{StockItems.Count} stock item(s) loaded · {response.Metadata.Freshness}";
        });

    [RelayCommand(CanExecute = nameof(CanRequestCompanyRefresh))]
    private Task RequestCompanyRefreshAsync(CancellationToken cancellationToken) => RefreshAsync(
        busy => IsFetchingCompanies = busy,
        "companies",
        [MasterTypes.Companies],
        token => FetchCompaniesAsync(announce: true, token),
        cancellationToken);

    [RelayCommand(CanExecute = nameof(CanRequestLedgerRefresh))]
    private Task RequestLedgerRefreshAsync(CancellationToken cancellationToken) => RefreshAsync(
        busy => IsFetchingLedgers = busy,
        "ledgers + voucher types",
        [MasterTypes.Ledgers, MasterTypes.VoucherTypes],
        token => FetchLedgersAndVoucherTypesAsync(announce: true, token),
        cancellationToken);

    [RelayCommand(CanExecute = nameof(CanRequestStockItemRefresh))]
    private Task RequestStockItemRefreshAsync(CancellationToken cancellationToken) => RefreshAsync(
        busy => IsFetchingStockItems = busy,
        "stock items",
        [MasterTypes.StockItems],
        token => FetchStockItemsAsync(announce: true, token),
        cancellationToken);

    [RelayCommand(CanExecute = nameof(CanSetActiveCompany))]
    private async Task SetActiveCompanyAsync(CancellationToken cancellationToken)
    {
        if (SelectedCompany is null) return;

        IsSettingActiveCompany = true;
        var targetName = SelectedCompany.Name;
        _shell.StatusMessage = $"Setting active company to {targetName}…";
        try
        {
            var response = await _settingsApi.SelectActiveCompanyAsync(
                new SelectActiveCompanyRequest(targetName), cancellationToken);
            _shell.SettingsSource = response.SettingsSource;
            _shell.Summary = response.Summary;
            _shell.UpdatedAtUtc = response.UpdatedAtUtc;
            await _shell.LoadAsync(cancellationToken);
            _shell.StatusMessage = $"Active company set to {targetName}.";
        }
        catch (Exception ex)
        {
            _shell.StatusMessage = $"Set active failed: {ApiResponseReader.FormatError(ex)}";
        }
        finally
        {
            IsSettingActiveCompany = false;
        }
    }

    /// <summary>
    /// Reads a cached snapshot; <paramref name="load"/> returns the status text to show.
    /// <paramref name="announce"/> is off for the lists Settings reads by itself when it opens:
    /// nobody asked for those, and their summary used to sit under whichever section was open,
    /// Print layout included. A read that fails is said either way.
    /// </summary>
    private async Task FetchAsync(bool announce, Action<bool> setBusy, string what, Func<Task<string>> load)
    {
        setBusy(true);
        if (announce) _shell.StatusMessage = $"Fetching {what}…";
        try
        {
            var summary = await load();
            if (announce) _shell.StatusMessage = summary;
        }
        catch (Exception ex)
        {
            _shell.StatusMessage = $"Fetch {what} failed: {ApiResponseReader.FormatError(ex)}";
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
        setBusy(true);
        _shell.StatusMessage = $"Refreshing {what} from Tally…";
        var refreshed = false;
        try
        {
            var results = new List<TallyMasterRefreshResult>();
            foreach (var masterType in masterTypes)
            {
                results.AddRange(await _mastersApi.RequestRefreshAsync(
                    new MasterRefreshRequest(MasterType: masterType),
                    cancellationToken));
            }

            refreshed = results.All(result => result.Succeeded);
            _shell.StatusMessage = SettingsRefreshSummaryFormatter.Summarize(results);
        }
        catch (Exception ex)
        {
            _shell.StatusMessage = $"Refresh failed: {ApiResponseReader.FormatError(ex)}";
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
