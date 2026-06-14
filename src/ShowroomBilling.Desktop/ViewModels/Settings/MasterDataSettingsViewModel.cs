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

public partial class MasterDataSettingsViewModel : ObservableObject, ISettingsMasterSnapshotHost
{
    private readonly ISettingsApiClient? _settingsApi;
    private readonly IMastersApiClient? _mastersApi;
    private readonly IMasterDataSettingsShell _shell;
    private readonly SettingsMasterSnapshotWorkflow _masterWorkflow;

    internal MasterDataSettingsViewModel(
        ISettingsApiClient? settingsApi,
        IMastersApiClient? mastersApi,
        IMasterDataSettingsShell shell)
    {
        _settingsApi = settingsApi;
        _mastersApi = mastersApi;
        _shell = shell;
        _masterWorkflow = new SettingsMasterSnapshotWorkflow(settingsApi, mastersApi, this, shell.LoadAsync);

        Companies = new ObservableCollection<CompanySnapshotItem>();
        LedgerOptions = new ObservableCollection<LedgerSnapshotItem>();
        VoucherTypeOptions = new ObservableCollection<VoucherTypeSnapshotItem>();
        StockItems = new ObservableCollection<StockItemSnapshotItem>();

        RequestCompanyRefreshCommand = new AsyncRelayCommand(RequestCompanyRefreshAsync,
            () => _mastersApi is not null && !IsFetchingCompanies && !IsSettingActiveCompany);
        SetActiveCompanyCommand = new AsyncRelayCommand(SetActiveCompanyAsync,
            () => _settingsApi is not null
                && SelectedCompany is not null
                && !IsSettingActiveCompany
                && !IsFetchingCompanies
                && !string.Equals(SelectedCompany.Name, ActiveCompanyName, StringComparison.Ordinal));
        FetchLedgersAndVoucherTypesCommand = new AsyncRelayCommand(FetchLedgersAndVoucherTypesAsync,
            () => _mastersApi is not null && !IsFetchingLedgers);
        RequestLedgerRefreshCommand = new AsyncRelayCommand(RequestLedgerRefreshAsync,
            () => _mastersApi is not null && !IsFetchingLedgers);
        FetchStockItemsCommand = new AsyncRelayCommand(FetchStockItemsAsync,
            () => _mastersApi is not null && !IsFetchingStockItems);
        RequestStockItemRefreshCommand = new AsyncRelayCommand(RequestStockItemRefreshAsync,
            () => _mastersApi is not null && !IsFetchingStockItems);

        AddItemMasterRowCommand = new RelayCommand(AddItemMasterRow, () => IsEditing);
        RemoveItemMasterRowCommand = new RelayCommand<ItemMasterRowVm>(RemoveItemMasterRow, _ => IsEditing);
        AddKaratRowCommand = new RelayCommand(AddKaratRow, () => IsEditing);
        RemoveKaratRowCommand = new RelayCommand<KaratMasterRowVm>(RemoveKaratRow, _ => IsEditing);
    }

    public SettingsDraft Draft => _shell.Draft;
    public bool IsEditing => _shell.IsEditing;
    public string ActiveCompanyName => _shell.ActiveCompanyName;

    public string StatusMessage
    {
        get => _shell.StatusMessage;
        set
        {
            if (_shell.StatusMessage == value) return;
            _shell.StatusMessage = value;
            OnPropertyChanged();
        }
    }

    public string SettingsSource
    {
        get => _shell.SettingsSource;
        set => _shell.SettingsSource = value;
    }

    public string Summary
    {
        get => _shell.Summary;
        set => _shell.Summary = value;
    }

    public DateTimeOffset? UpdatedAtUtc
    {
        get => _shell.UpdatedAtUtc;
        set => _shell.UpdatedAtUtc = value;
    }

    public IAsyncRelayCommand RequestCompanyRefreshCommand { get; }
    public IAsyncRelayCommand SetActiveCompanyCommand { get; }
    public IAsyncRelayCommand FetchLedgersAndVoucherTypesCommand { get; }
    public IAsyncRelayCommand RequestLedgerRefreshCommand { get; }
    public IAsyncRelayCommand FetchStockItemsCommand { get; }
    public IAsyncRelayCommand RequestStockItemRefreshCommand { get; }
    public IRelayCommand AddItemMasterRowCommand { get; }
    public IRelayCommand<ItemMasterRowVm> RemoveItemMasterRowCommand { get; }
    public IRelayCommand AddKaratRowCommand { get; }
    public IRelayCommand<KaratMasterRowVm> RemoveKaratRowCommand { get; }

    public ObservableCollection<CompanySnapshotItem> Companies { get; }
    public ObservableCollection<LedgerSnapshotItem> LedgerOptions { get; }
    public ObservableCollection<VoucherTypeSnapshotItem> VoucherTypeOptions { get; }
    public ObservableCollection<StockItemSnapshotItem> StockItems { get; }

    public IReadOnlyList<string> ItemUnitOptions { get; } = ItemUnits.All;
    public IReadOnlyList<string> ItemCategoryOptions { get; } = ItemCategories.All;
    public IReadOnlyList<string> PricingModeOptions { get; } = PricingModes.All;

    [ObservableProperty] private CompanySnapshotItem? selectedCompany;
    [ObservableProperty] private bool isFetchingCompanies;
    [ObservableProperty] private bool isSettingActiveCompany;
    [ObservableProperty] private string companiesFreshness = "—";
    [ObservableProperty] private DateTimeOffset? companiesFetchedAtUtc;
    [ObservableProperty] private int companiesCount;

    [ObservableProperty] private bool isFetchingLedgers;
    [ObservableProperty] private string ledgersFreshness = "—";
    [ObservableProperty] private DateTimeOffset? ledgersFetchedAtUtc;
    [ObservableProperty] private int ledgersCount;
    [ObservableProperty] private int voucherTypesCount;

    [ObservableProperty] private bool isFetchingStockItems;
    [ObservableProperty] private string stockItemsFreshness = "—";
    [ObservableProperty] private DateTimeOffset? stockItemsFetchedAtUtc;
    [ObservableProperty] private int stockItemsCount;

    partial void OnIsFetchingCompaniesChanged(bool value) => NotifyCompanyCommandsChanged();
    partial void OnIsSettingActiveCompanyChanged(bool value) => NotifyCompanyCommandsChanged();
    partial void OnSelectedCompanyChanged(CompanySnapshotItem? value) => SetActiveCompanyCommand.NotifyCanExecuteChanged();

    partial void OnIsFetchingLedgersChanged(bool value)
    {
        FetchLedgersAndVoucherTypesCommand.NotifyCanExecuteChanged();
        RequestLedgerRefreshCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsFetchingStockItemsChanged(bool value)
    {
        FetchStockItemsCommand.NotifyCanExecuteChanged();
        RequestStockItemRefreshCommand.NotifyCanExecuteChanged();
    }

    public async Task LoadMissingSnapshotsAsync(CancellationToken cancellationToken)
    {
        if (_mastersApi is null) return;

        var snapshotLoads = new List<Task>(capacity: 3);
        if (Companies.Count == 0)
            snapshotLoads.Add(FetchCompaniesAsync(cancellationToken));
        if (LedgerOptions.Count == 0)
            snapshotLoads.Add(FetchLedgersAndVoucherTypesAsync(cancellationToken));
        if (StockItems.Count == 0)
            snapshotLoads.Add(FetchStockItemsAsync(cancellationToken));

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

    private void AddItemMasterRow() => Draft.ItemMasterRows.Add(new ItemMasterRowVm());

    private void RemoveItemMasterRow(ItemMasterRowVm? row)
    {
        if (row is not null) Draft.ItemMasterRows.Remove(row);
    }

    private void AddKaratRow() => Draft.KaratRows.Add(new KaratMasterRowVm());

    private void RemoveKaratRow(KaratMasterRowVm? row)
    {
        if (row is not null) Draft.KaratRows.Remove(row);
    }

    private async Task FetchCompaniesAsync(CancellationToken cancellationToken)
        => await _masterWorkflow.FetchCompaniesAsync(cancellationToken);

    private async Task RequestCompanyRefreshAsync(CancellationToken cancellationToken)
        => await _masterWorkflow.RequestCompanyRefreshAsync(cancellationToken);

    private async Task SetActiveCompanyAsync(CancellationToken cancellationToken)
        => await _masterWorkflow.SetActiveCompanyAsync(cancellationToken);

    private async Task FetchLedgersAndVoucherTypesAsync(CancellationToken cancellationToken)
        => await _masterWorkflow.FetchLedgersAndVoucherTypesAsync(cancellationToken);

    private async Task RequestLedgerRefreshAsync(CancellationToken cancellationToken)
        => await _masterWorkflow.RequestLedgerRefreshAsync(cancellationToken);

    private async Task FetchStockItemsAsync(CancellationToken cancellationToken)
        => await _masterWorkflow.FetchStockItemsAsync(cancellationToken);

    private async Task RequestStockItemRefreshAsync(CancellationToken cancellationToken)
        => await _masterWorkflow.RequestStockItemRefreshAsync(cancellationToken);
}
