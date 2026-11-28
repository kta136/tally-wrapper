using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShowroomBilling.Contracts.Masters;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Desktop.Services;

namespace ShowroomBilling.Desktop.ViewModels.Settings;

internal interface IMasterDataSettingsShell
{
    SettingsDraft Draft { get; }
    bool IsEditing { get; }
    string ActiveCompanyName { get; }
    string StatusMessage { get; set; }
    string SettingsSource { get; set; }
    string Summary { get; set; }
    DateTimeOffset? UpdatedAtUtc { get; set; }
    Task LoadAsync(CancellationToken cancellationToken = default);
}

public partial class MasterDataSettingsViewModel : ObservableObject
{
    private readonly IMastersApiClient _mastersApi;
    private readonly IMasterDataSettingsShell _shell;
    private readonly ISettingsApiClient _settingsApi;

    internal MasterDataSettingsViewModel(
        ISettingsApiClient settingsApi,
        IMastersApiClient mastersApi,
        IMasterDataSettingsShell shell)
    {
        _mastersApi = mastersApi;
        _shell = shell;
        _settingsApi = settingsApi;

        Companies = new ObservableCollection<CompanySnapshotItem>();
        LedgerOptions = new ObservableCollection<LedgerSnapshotItem>();
        VoucherTypeOptions = new ObservableCollection<VoucherTypeSnapshotItem>();
        StockItems = new ObservableCollection<StockItemSnapshotItem>();
    }

    public SettingsDraft Draft => _shell.Draft;
    public bool IsEditing => _shell.IsEditing;
    public string ActiveCompanyName => _shell.ActiveCompanyName;

    public ObservableCollection<CompanySnapshotItem> Companies { get; }
    public ObservableCollection<LedgerSnapshotItem> LedgerOptions { get; }
    public ObservableCollection<VoucherTypeSnapshotItem> VoucherTypeOptions { get; }
    public ObservableCollection<StockItemSnapshotItem> StockItems { get; }

    public IReadOnlyList<string> ItemCategoryOptions { get; } = ItemCategories.All;
    public IReadOnlyList<string> PricingModeOptions { get; } = PricingModes.All;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SetActiveCompanyCommand))]
    private CompanySnapshotItem? selectedCompany;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RequestCompanyRefreshCommand), nameof(SetActiveCompanyCommand))]
    private bool isFetchingCompanies;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RequestCompanyRefreshCommand), nameof(SetActiveCompanyCommand))]
    private bool isSettingActiveCompany;

    [ObservableProperty] private string companiesFreshness = "—";
    [ObservableProperty] private DateTimeOffset? companiesFetchedAtUtc;
    [ObservableProperty] private int companiesCount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RequestLedgerRefreshCommand))]
    private bool isFetchingLedgers;

    [ObservableProperty] private string ledgersFreshness = "—";
    [ObservableProperty] private DateTimeOffset? ledgersFetchedAtUtc;
    [ObservableProperty] private int ledgersCount;
    [ObservableProperty] private int voucherTypesCount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RequestStockItemRefreshCommand))]
    private bool isFetchingStockItems;

    [ObservableProperty] private string stockItemsFreshness = "—";
    [ObservableProperty] private DateTimeOffset? stockItemsFetchedAtUtc;
    [ObservableProperty] private int stockItemsCount;

    private bool CanRequestCompanyRefresh => !IsFetchingCompanies && !IsSettingActiveCompany;

    private bool CanSetActiveCompany =>
        SelectedCompany is not null
        && !IsSettingActiveCompany
        && !IsFetchingCompanies
        && !string.Equals(SelectedCompany.Name, ActiveCompanyName, StringComparison.Ordinal);

    private bool CanRequestLedgerRefresh => !IsFetchingLedgers;

    private bool CanRequestStockItemRefresh => !IsFetchingStockItems;

    public async Task LoadMissingSnapshotsAsync(CancellationToken cancellationToken)
    {
        var snapshotLoads = new List<Task>(capacity: 3);
        if (Companies.Count == 0)
            snapshotLoads.Add(FetchCompaniesAsync(announce: false, cancellationToken));
        if (LedgerOptions.Count == 0)
            snapshotLoads.Add(FetchLedgersAndVoucherTypesAsync(announce: false, cancellationToken));
        if (StockItems.Count == 0)
            snapshotLoads.Add(FetchStockItemsAsync(announce: false, cancellationToken));

        if (snapshotLoads.Count > 0)
            await Task.WhenAll(snapshotLoads);
    }

    public void NotifyShellStateChanged()
    {
        OnPropertyChanged(nameof(Draft));
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(ActiveCompanyName));
        NotifyCompanyCommandsChanged();
        NotifyMasterRowCommandsChanged();
    }

    private void NotifyCompanyCommandsChanged()
    {
        RequestCompanyRefreshCommand.NotifyCanExecuteChanged();
        SetActiveCompanyCommand.NotifyCanExecuteChanged();
    }

    private void NotifyMasterRowCommandsChanged()
    {
        AddItemMasterRowCommand.NotifyCanExecuteChanged();
        RemoveItemMasterRowCommand.NotifyCanExecuteChanged();
        AddKaratRowCommand.NotifyCanExecuteChanged();
        RemoveKaratRowCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(IsEditing))]
    private void AddItemMasterRow() => Draft.ItemMasterRows.Add(new ItemMasterRowVm());

    [RelayCommand(CanExecute = nameof(IsEditing))]
    private void RemoveItemMasterRow(ItemMasterRowVm? row)
    {
        if (row is not null) Draft.ItemMasterRows.Remove(row);
    }

    [RelayCommand(CanExecute = nameof(IsEditing))]
    private void AddKaratRow() => Draft.KaratRows.Add(new KaratMasterRowVm());

    [RelayCommand(CanExecute = nameof(IsEditing))]
    private void RemoveKaratRow(KaratMasterRowVm? row)
    {
        if (row is not null) Draft.KaratRows.Remove(row);
    }
}
