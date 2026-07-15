using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Desktop.Configuration;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.Services.ProcessSupervision;
using ShowroomBilling.Desktop.ViewModels.Admin;

namespace ShowroomBilling.Desktop.ViewModels.Settings;

public partial class SettingsViewModel : ObservableObject,
    ISettingsEditWorkflowHost,
    ISettingsSectionHost,
    IMasterDataSettingsShell
{
    private readonly ISettingsApiClient? _settingsApi;
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
        IHealthApiClient? healthApi = null,
        AdminTokenStore? adminTokenStore = null,
        IChildProcessSupervisor? childProcessSupervisor = null,
        DesktopBootstrapOptions? bootstrapOptions = null,
        Action? restartApplication = null,
        Func<bool>? confirmConnectionModeRestart = null)
        : this(settingsApi, mastersApi, printAssetApi,
            (draft, layout, host) =>
                new SettingsPreviewViewModel(draft, layout, host, printDispatcher, printAssetApi, printPreferences),
            runtimeApi,
            healthApi,
            adminTokenStore,
            childProcessSupervisor,
            bootstrapOptions,
            restartApplication,
            confirmConnectionModeRestart)
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
        IHealthApiClient? healthApi = null,
        AdminTokenStore? adminTokenStore = null,
        IChildProcessSupervisor? childProcessSupervisor = null,
        DesktopBootstrapOptions? bootstrapOptions = null,
        Action? restartApplication = null,
        Func<bool>? confirmConnectionModeRestart = null)
    {
        ArgumentNullException.ThrowIfNull(previewFactory);
        _settingsApi = settingsApi;
        Draft = new SettingsDraft();
        Draft.PropertyChanged += OnDraftPropertyChanged;

        Database = new DatabaseSettingsViewModel(
            runtimeApi,
            healthApi,
            adminTokenStore,
            childProcessSupervisor,
            bootstrapOptions,
            restartApplication,
            confirmConnectionModeRestart,
            () => IsDirty);
        MasterData = new MasterDataSettingsViewModel(_settingsApi, mastersApi, this);
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

        Preview = previewFactory(Draft, PrintLayout, this);
        _sectionCoordinator.UpdatePreviewActivation();

        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(CancellationToken.None), () => !IsLoading && !IsEditing);
        BeginEditCommand = new RelayCommand(BeginEdit, () => !IsEditing && !IsLoading && Settings is not null);
        DiscardChangesCommand = new RelayCommand(DiscardChanges, () => IsEditing && !IsSaving);
        SaveAllCommand = new AsyncRelayCommand(SaveAsync, () => IsEditing && IsDirty && !IsSaving);

        OpenLogFolderCommand = new RelayCommand(OpenLogFolder);
        OpenInstallFolderCommand = new RelayCommand(OpenInstallFolder);
        OpenAppDataFolderCommand = new RelayCommand(OpenAppDataFolder);
        DensityOptions = [UiDensityManager.Compact, UiDensityManager.Comfortable];
        UiDensity = UiDensityManager.CurrentDensity;
    }

    public IRelayCommand OpenLogFolderCommand { get; }
    public IRelayCommand OpenInstallFolderCommand { get; }
    public IRelayCommand OpenAppDataFolderCommand { get; }
    public IReadOnlyList<string> DensityOptions { get; }

    public Func<CancellationToken, Task>? AdminUnlockHandler
    {
        get => Database.AdminUnlockHandler;
        set => Database.AdminUnlockHandler = value;
    }

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
    public DatabaseSettingsViewModel Database { get; }
    public MasterDataSettingsViewModel MasterData { get; }
    public SettingsDraft Draft { get; }
    public PrintLayoutViewModel PrintLayout { get; }
    public SettingsPreviewViewModel Preview { get; }

    public IAsyncRelayCommand RefreshCommand { get; }
    public IRelayCommand BeginEditCommand { get; }
    public IRelayCommand DiscardChangesCommand { get; }
    public IAsyncRelayCommand SaveAllCommand { get; }

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
    [ObservableProperty] private string uiDensity = UiDensityManager.Compact;

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

    /// <summary>Show the live preview pane only for Invoice and Print Layout sections.</summary>
    public bool IsPreviewVisible => _sectionCoordinator.IsPreviewVisible;

    public string ActiveCompanyName => Settings?.Connection.ActiveCompanyName ?? string.Empty;

    partial void OnSelectedSectionChanged(SettingsSectionKey value)
        => _sectionCoordinator.OnSelectedSectionChanged();

    partial void OnIsLoadingChanged(bool value) => NotifyCommandsChanged();
    partial void OnIsSavingChanged(bool value) => NotifyCommandsChanged();
    partial void OnIsEditingChanged(bool value)
    {
        NotifyCommandsChanged();
        MasterData.NotifyShellStateChanged();
    }
    partial void OnIsDirtyChanged(bool value) => SaveAllCommand.NotifyCanExecuteChanged();
    partial void OnUiDensityChanged(string value) => UiDensityManager.ApplyDensity(value);

    partial void OnSettingsChanged(EffectiveCloudSettingsDto? value)
    {
        OnPropertyChanged(nameof(ActiveCompanyName));
        BeginEditCommand.NotifyCanExecuteChanged();
        MasterData.NotifyShellStateChanged();
    }

    private void NotifyCommandsChanged()
    {
        RefreshCommand.NotifyCanExecuteChanged();
        BeginEditCommand.NotifyCanExecuteChanged();
        DiscardChangesCommand.NotifyCanExecuteChanged();
        SaveAllCommand.NotifyCanExecuteChanged();
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
            await MasterData.LoadMissingSnapshotsAsync(cancellationToken);
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
        => await Database.LoadDatabaseConfigAsync(cancellationToken);

    private void BeginEdit()
        => _editWorkflow.BeginEdit();

    private void DiscardChanges()
        => _editWorkflow.DiscardChanges();

    private async Task SaveAsync(CancellationToken cancellationToken)
        => await _editWorkflow.SaveAsync(cancellationToken);

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
