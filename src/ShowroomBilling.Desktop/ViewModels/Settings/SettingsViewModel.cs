using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShowroomBilling.Contracts.Masters;
using ShowroomBilling.Contracts.Runtime;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.Services.ProcessSupervision;
using ShowroomBilling.Desktop.ViewModels.Admin;

namespace ShowroomBilling.Desktop.ViewModels.Settings;

public partial class SettingsViewModel : ObservableObject,
    IDatabaseConfigurationWorkflowHost,
    ISettingsMasterSnapshotHost,
    ISettingsEditWorkflowHost,
    ISettingsSectionHost
{
    private readonly ISettingsApiClient? _settingsApi;
    private readonly IMastersApiClient? _mastersApi;
    private readonly DatabaseConfigurationWorkflow _databaseWorkflow;
    private readonly SettingsMasterSnapshotWorkflow _masterWorkflow;
    private readonly SettingsEditWorkflow _editWorkflow;
    private readonly SettingsSectionCoordinator _sectionCoordinator;
    private readonly object _loadGate = new();
    private Task? _loadTask;

    public SettingsViewModel() : this(null, null, null, (IPrintDispatcher?)null) { }

    public SettingsViewModel(ISettingsApiClient? settingsApi, IMastersApiClient? mastersApi)
        : this(settingsApi, mastersApi, null, (IPrintDispatcher?)null) { }

    public SettingsViewModel(
        ISettingsApiClient? settingsApi,
        IMastersApiClient? mastersApi,
        IPrintAssetApiClient? printAssetApi)
        : this(settingsApi, mastersApi, printAssetApi, (IPrintDispatcher?)null) { }

    public SettingsViewModel(
        ISettingsApiClient? settingsApi,
        IMastersApiClient? mastersApi,
        IPrintAssetApiClient? printAssetApi,
        IPrintDispatcher? printDispatcher)
        : this(settingsApi, mastersApi, printAssetApi, printDispatcher, null)
    { }

    public SettingsViewModel(
        ISettingsApiClient? settingsApi,
        IMastersApiClient? mastersApi,
        IPrintAssetApiClient? printAssetApi,
        IPrintDispatcher? printDispatcher,
        IPrintPreferencesStore? printPreferences,
        IRuntimeApiClient? runtimeApi = null,
        AdminTokenStore? adminTokenStore = null,
        ChildProcessSupervisor? childProcessSupervisor = null)
        : this(settingsApi, mastersApi, printAssetApi,
            (draft, layout, host) =>
                new SettingsPreviewViewModel(draft, layout, host, printDispatcher, printAssetApi, printPreferences),
            runtimeApi,
            adminTokenStore,
            childProcessSupervisor)
    { }

    /// <summary>Internal ctor that accepts a preview factory. Tests inject a factory
    /// so the embedded <see cref="Preview"/> is the one under test — avoiding the
    /// double subscription you get from constructing a second preview over the same
    /// draft/layout.</summary>
    internal SettingsViewModel(
        ISettingsApiClient? settingsApi,
        IMastersApiClient? mastersApi,
        IPrintAssetApiClient? printAssetApi,
        Func<SettingsDraft, PrintLayoutViewModel, SettingsViewModel, SettingsPreviewViewModel> previewFactory,
        IRuntimeApiClient? runtimeApi = null,
        AdminTokenStore? adminTokenStore = null,
        ChildProcessSupervisor? childProcessSupervisor = null)
    {
        ArgumentNullException.ThrowIfNull(previewFactory);
        _settingsApi = settingsApi;
        _mastersApi = mastersApi;
        _databaseWorkflow = new DatabaseConfigurationWorkflow(runtimeApi, adminTokenStore, childProcessSupervisor, this);
        _masterWorkflow = new SettingsMasterSnapshotWorkflow(_settingsApi, _mastersApi, this, LoadAsync);
        _editWorkflow = new SettingsEditWorkflow(_settingsApi, this);
        _sectionCoordinator = new SettingsSectionCoordinator(this);

        PrintLayout = new PrintLayoutViewModel(settingsApi, printAssetApi);

        // Admin is added conditionally once AdminVm is wired and unlocked —
        // see SyncAdminSection. This keeps the sidebar from advertising admin
        // capabilities to an operator who doesn't have a session.
        Sections = new ObservableCollection<SettingsSectionKey>
        {
            SettingsSectionKey.Database,
            SettingsSectionKey.Connection,
            SettingsSectionKey.Invoice,
            SettingsSectionKey.PrintLayout,
            SettingsSectionKey.Ledgers,
            SettingsSectionKey.Masters,
            SettingsSectionKey.Advanced,
        };
        SelectedSection = SettingsSectionKey.Database;

        Draft = new SettingsDraft();
        Draft.PropertyChanged += OnDraftPropertyChanged;

        Preview = previewFactory(Draft, PrintLayout, this);
        _sectionCoordinator.UpdatePreviewActivation();

        Companies = new ObservableCollection<CompanySnapshotItem>();
        LedgerOptions = new ObservableCollection<LedgerSnapshotItem>();
        VoucherTypeOptions = new ObservableCollection<VoucherTypeSnapshotItem>();
        StockItems = new ObservableCollection<StockItemSnapshotItem>();

        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(CancellationToken.None), () => !IsLoading && !IsEditing);
        BeginEditCommand = new RelayCommand(BeginEdit, () => !IsEditing && !IsLoading && Settings is not null);
        DiscardChangesCommand = new RelayCommand(DiscardChanges, () => IsEditing && !IsSaving);
        SaveAllCommand = new AsyncRelayCommand(SaveAsync, () => IsEditing && IsDirty && !IsSaving);
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

        OpenLogFolderCommand = new RelayCommand(OpenLogFolder);
        OpenInstallFolderCommand = new RelayCommand(OpenInstallFolder);
        OpenAppDataFolderCommand = new RelayCommand(OpenAppDataFolder);
        LoadDatabaseConfigCommand = new AsyncRelayCommand(LoadDatabaseConfigAsync, () => !IsDatabaseConfigBusy);
        TestDatabaseConnectionCommand = new AsyncRelayCommand(TestDatabaseConnectionAsync, CanUseDatabaseConfigCommands);
        SaveDatabaseConfigCommand = new AsyncRelayCommand(SaveDatabaseConfigAsync, CanUseDatabaseConfigCommands);
        RestartApiCommand = new AsyncRelayCommand(RestartApiAsync, () => CanRestartApi);
    }

    public IRelayCommand OpenLogFolderCommand { get; }
    public IRelayCommand OpenInstallFolderCommand { get; }
    public IRelayCommand OpenAppDataFolderCommand { get; }
    public IAsyncRelayCommand LoadDatabaseConfigCommand { get; }
    public IAsyncRelayCommand TestDatabaseConnectionCommand { get; }
    public IAsyncRelayCommand SaveDatabaseConfigCommand { get; }
    public IAsyncRelayCommand RestartApiCommand { get; }

    public Func<CancellationToken, Task>? AdminUnlockHandler { get; set; }

    /// <summary>
    /// Settable by the shell (MainWindowViewModel) so the Admin section of
    /// Settings can bind to the same AdminUnlockViewModel instance the unlock
    /// dialog uses. Optional — tests and the parameterless ctor leave it null
    /// and the Admin tab simply shows a "not wired" banner.
    /// </summary>
    public AdminUnlockViewModel? AdminVm
    {
        get => _adminVm;
        set
        {
            if (ReferenceEquals(_adminVm, value)) return;

            if (_adminVm is not null)
            {
                _adminVm.PropertyChanged -= OnAdminVmPropertyChanged;
            }

            _adminVm = value;

            if (_adminVm is not null)
            {
                _adminVm.PropertyChanged += OnAdminVmPropertyChanged;
            }

            OnPropertyChanged(nameof(AdminVm));
            OnPropertyChanged(nameof(IsAdminVmWired));
            OnPropertyChanged(nameof(IsAdminFeaturesVisible));
            _sectionCoordinator.SyncAdminSection(_adminVm);
        }
    }
    private AdminUnlockViewModel? _adminVm;

    public bool IsAdminVmWired => _adminVm is not null;

    /// <summary>
    /// Tabs and controls that are functionally admin-only (Admin tab itself,
    /// synthetic batch scheduler button, etc.) bind to this so they hide when
    /// no admin session is active. Follows the navbar ADMIN chip's visibility.
    /// </summary>
    public bool IsAdminFeaturesVisible => _adminVm?.IsUnlocked == true;

    private void OnAdminVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AdminUnlockViewModel.IsUnlocked) or nameof(AdminUnlockViewModel.IsLocked))
        {
            OnPropertyChanged(nameof(IsAdminFeaturesVisible));
            _sectionCoordinator.SyncAdminSection(_adminVm);
        }
    }

    public string LogFolderPath => SettingsFolderLauncher.LogFolderPath;
    public string InstallFolderPath => AppContext.BaseDirectory;
    public string AppDataFolderPath => SettingsFolderLauncher.AppDataFolderPath;

    private static void OpenLogFolder() => SettingsFolderLauncher.OpenLogFolder();

    private static void OpenInstallFolder() => SettingsFolderLauncher.OpenInstallFolder();

    private static void OpenAppDataFolder() => SettingsFolderLauncher.OpenAppDataFolder();

    public ObservableCollection<SettingsSectionKey> Sections { get; }
    public SettingsDraft Draft { get; }
    public PrintLayoutViewModel PrintLayout { get; }
    public SettingsPreviewViewModel Preview { get; }

    public IAsyncRelayCommand RefreshCommand { get; }
    public IRelayCommand BeginEditCommand { get; }
    public IRelayCommand DiscardChangesCommand { get; }
    public IAsyncRelayCommand SaveAllCommand { get; }
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

    [ObservableProperty] private SettingsSectionKey selectedSection;
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private bool isSaving;
    [ObservableProperty] private bool isEditing;
    [ObservableProperty] private bool isDirty;
    [ObservableProperty] private string statusMessage = string.Empty;
    [ObservableProperty] private string settingsSource = "—";
    [ObservableProperty] private string summary = string.Empty;
    [ObservableProperty] private DateTimeOffset? updatedAtUtc;
    [ObservableProperty] private EffectiveCloudSettingsDto? settings;

    [ObservableProperty] private string databaseConnectionString = string.Empty;
    [ObservableProperty] private string databaseMaskedConnectionString = "—";
    [ObservableProperty] private string databaseConfigPath = "—";
    [ObservableProperty] private string databaseConfigStatus = string.Empty;
    [ObservableProperty] private bool isDatabaseConfigBusy;
    [ObservableProperty] private bool isTestingDatabaseConnection;
    [ObservableProperty] private bool isSavingDatabaseConfig;
    [ObservableProperty] private bool isRestartingApi;
    [ObservableProperty] private bool isLocalDatabaseOverridePresent;
    [ObservableProperty] private bool databaseConfigRequiresRestart;

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

    public IReadOnlyList<string> CloudOwnedCategories { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<string> LocalOnlyCategories { get; private set; } = Array.Empty<string>();

    public bool IsConnectionVisible => _sectionCoordinator.IsConnectionVisible;
    public bool IsDatabaseVisible => _sectionCoordinator.IsDatabaseVisible;
    public bool IsInvoiceVisible => _sectionCoordinator.IsInvoiceVisible;
    public bool IsPrintLayoutVisible => _sectionCoordinator.IsPrintLayoutVisible;
    public bool IsLedgersVisible => _sectionCoordinator.IsLedgersVisible;
    public bool IsMastersVisible => _sectionCoordinator.IsMastersVisible;
    public bool IsAdvancedVisible => _sectionCoordinator.IsAdvancedVisible;
    public bool IsAdminVisible => _sectionCoordinator.IsAdminVisible;
    public bool CanRestartApi => _databaseWorkflow.CanRestartApi;

    /// <summary>Show the live preview pane only for Invoice and Print Layout sections.</summary>
    public bool IsPreviewVisible => _sectionCoordinator.IsPreviewVisible;

    public string ActiveCompanyName => Settings?.Connection.ActiveCompanyName ?? string.Empty;
    public int ItemMasterRowCount => CountJsonRows(Settings?.Masters.ItemMasterDataJson);
    public int KaratMappingRowCount => CountJsonRows(Settings?.Masters.KaratMappingDataJson);

    partial void OnSelectedSectionChanged(SettingsSectionKey value)
        => _sectionCoordinator.OnSelectedSectionChanged();

    partial void OnIsLoadingChanged(bool value) => NotifyCommandsChanged();
    partial void OnIsSavingChanged(bool value) => NotifyCommandsChanged();
    partial void OnIsEditingChanged(bool value) => NotifyCommandsChanged();
    partial void OnIsDirtyChanged(bool value) => SaveAllCommand.NotifyCanExecuteChanged();
    partial void OnDatabaseConnectionStringChanged(string value) => NotifyDatabaseConfigCommandsChanged();
    partial void OnIsDatabaseConfigBusyChanged(bool value) => NotifyDatabaseConfigCommandsChanged();
    partial void OnIsTestingDatabaseConnectionChanged(bool value) => NotifyDatabaseConfigCommandsChanged();
    partial void OnIsSavingDatabaseConfigChanged(bool value) => NotifyDatabaseConfigCommandsChanged();
    partial void OnIsRestartingApiChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRestartApi));
        RestartApiCommand.NotifyCanExecuteChanged();
    }

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

    partial void OnSettingsChanged(EffectiveCloudSettingsDto? value)
    {
        OnPropertyChanged(nameof(ActiveCompanyName));
        OnPropertyChanged(nameof(ItemMasterRowCount));
        OnPropertyChanged(nameof(KaratMappingRowCount));
        BeginEditCommand.NotifyCanExecuteChanged();
        SetActiveCompanyCommand.NotifyCanExecuteChanged();
    }

    private void NotifyCommandsChanged()
    {
        RefreshCommand.NotifyCanExecuteChanged();
        BeginEditCommand.NotifyCanExecuteChanged();
        DiscardChangesCommand.NotifyCanExecuteChanged();
        SaveAllCommand.NotifyCanExecuteChanged();
        AddItemMasterRowCommand.NotifyCanExecuteChanged();
        RemoveItemMasterRowCommand.NotifyCanExecuteChanged();
        AddKaratRowCommand.NotifyCanExecuteChanged();
        RemoveKaratRowCommand.NotifyCanExecuteChanged();
        NotifyDatabaseConfigCommandsChanged();
    }

    private void NotifyDatabaseConfigCommandsChanged()
    {
        LoadDatabaseConfigCommand.NotifyCanExecuteChanged();
        TestDatabaseConnectionCommand.NotifyCanExecuteChanged();
        SaveDatabaseConfigCommand.NotifyCanExecuteChanged();
        RestartApiCommand.NotifyCanExecuteChanged();
    }

    public void NotifySectionPropertiesChanged()
    {
        OnPropertyChanged(nameof(IsConnectionVisible));
        OnPropertyChanged(nameof(IsDatabaseVisible));
        OnPropertyChanged(nameof(IsInvoiceVisible));
        OnPropertyChanged(nameof(IsPrintLayoutVisible));
        OnPropertyChanged(nameof(IsLedgersVisible));
        OnPropertyChanged(nameof(IsMastersVisible));
        OnPropertyChanged(nameof(IsAdvancedVisible));
        OnPropertyChanged(nameof(IsAdminVisible));
        OnPropertyChanged(nameof(IsPreviewVisible));
    }

    private bool CanUseDatabaseConfigCommands() => _databaseWorkflow.CanUseCommands();

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

    private void NotifyCompanyCommandsChanged()
    {
        RequestCompanyRefreshCommand.NotifyCanExecuteChanged();
        SetActiveCompanyCommand.NotifyCanExecuteChanged();
    }

    private void OnDraftPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _editWorkflow.MarkDirtyIfEditing();
    }

    public void ApplyEffectiveSettings(EffectiveSettingsResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        Settings = response.Settings;
        SettingsSource = response.SettingsSource;
        Summary = response.Summary;
        UpdatedAtUtc = response.UpdatedAtUtc;
        CloudOwnedCategories = response.CloudOwnedCategories;
        LocalOnlyCategories = response.LocalOnlyCategories;
        OnPropertyChanged(nameof(CloudOwnedCategories));
        OnPropertyChanged(nameof(LocalOnlyCategories));

        Draft.LoadFrom(response.Settings);
        IsDirty = false;
        IsEditing = false;
        StatusMessage = string.Empty;
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            await Task.FromCanceled(cancellationToken);
            return;
        }

        Task loadTask;
        lock (_loadGate)
        {
            if (_loadTask is { IsCompleted: false } inFlight)
            {
                loadTask = inFlight;
            }
            else
            {
                loadTask = LoadCoreAsync(cancellationToken);
                _loadTask = loadTask;
            }
        }

        await loadTask;
    }

    private async Task LoadCoreAsync(CancellationToken cancellationToken = default)
    {
        await LoadDatabaseConfigAsync(cancellationToken);

        if (_settingsApi is null)
        {
            StatusMessage = "Settings API unavailable.";
            return;
        }

        IsLoading = true;
        StatusMessage = "Loading settings…";
        try
        {
            var response = await _settingsApi.GetEffectiveSettingsAsync(cancellationToken);
            ApplyEffectiveSettings(response);

            // Populate cached snapshots automatically so the operator doesn't
            // need manual "Fetch" clicks on each section. The per-section
            // "Refresh from Tally" button now re-pulls and re-fetches in one go.
            if (_mastersApi is not null)
            {
                if (Companies.Count == 0)
                    await FetchCompaniesAsync(cancellationToken);
                if (LedgerOptions.Count == 0)
                    await FetchLedgersAndVoucherTypesAsync(cancellationToken);
                if (StockItems.Count == 0)
                    await FetchStockItemsAsync(cancellationToken);
            }
        }
        catch (HttpRequestException ex)
        {
            StatusMessage = $"Load failed: {ApiResponseReader.FormatError(ex)}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Load failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task LoadDatabaseConfigAsync(CancellationToken cancellationToken = default)
        => await _databaseWorkflow.LoadAsync(cancellationToken);

    private async Task TestDatabaseConnectionAsync(CancellationToken cancellationToken)
        => await _databaseWorkflow.TestAsync(cancellationToken);

    private async Task SaveDatabaseConfigAsync(CancellationToken cancellationToken)
        => await _databaseWorkflow.SaveAsync(cancellationToken);

    private async Task RestartApiAsync(CancellationToken cancellationToken)
        => await _databaseWorkflow.RestartApiAsync(cancellationToken);

    private void BeginEdit()
        => _editWorkflow.BeginEdit();

    private void DiscardChanges()
        => _editWorkflow.DiscardChanges();

    private async Task SaveAsync(CancellationToken cancellationToken)
        => await _editWorkflow.SaveAsync(cancellationToken);

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

    private static int CountJsonRows(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return 0;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array
                ? doc.RootElement.GetArrayLength()
                : 0;
        }
        catch
        {
            return 0;
        }
    }

}

public enum SettingsSectionKey
{
    Database,
    Connection,
    Invoice,
    PrintLayout,
    Ledgers,
    Masters,
    Advanced,
    Admin,
}
