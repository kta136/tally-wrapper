using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Desktop.Configuration;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.ViewModels.Admin;

namespace ShowroomBilling.Desktop.ViewModels.Settings;

public partial class SettingsViewModel : ObservableObject,
    ISettingsEditWorkflowHost,
    ISettingsSectionHost,
    IMasterDataSettingsShell
{
    private readonly ISettingsApiClient _settingsApi;
    private readonly SettingsEditWorkflow _editWorkflow;
    private readonly SettingsSectionCoordinator _sectionCoordinator;
    private readonly object _loadGate = new();
    private Task? _loadTask;

    /// <param name="previewFactory">
    /// Builds the embedded <see cref="Preview"/>. Tests pass one so the preview
    /// under test is the one this view model owns, not a second subscriber
    /// over the same draft and layout.
    /// </param>
    public SettingsViewModel(
        ISettingsApiClient settingsApi,
        IMastersApiClient mastersApi,
        IPrintAssetApiClient printAssetApi,
        IDialogService dialogs,
        IPrintDispatcher? printDispatcher = null,
        IPrintPreferencesStore? printPreferences = null,
        DesktopBootstrapOptions? bootstrapOptions = null,
        Action? restartApplication = null,
        Func<SettingsDraft, PrintLayoutViewModel, SettingsViewModel, SettingsPreviewViewModel>? previewFactory = null,
        HandoverKeyStore? handoverKeys = null)
    {
        _settingsApi = settingsApi;
        Handover = new HandoverSettingsViewModel(handoverKeys ?? new HandoverKeyStore(), dialogs);
        Draft = new SettingsDraft();
        Draft.PropertyChanged += OnDraftPropertyChanged;

        Server = new ServerSettingsViewModel(
            dialogs,
            bootstrapOptions,
            restartApplication,
            hasUnsavedSettingsEdits: () => IsDirty);
        MasterData = new MasterDataSettingsViewModel(_settingsApi, mastersApi, this);
        _editWorkflow = new SettingsEditWorkflow(_settingsApi, this);
        _sectionCoordinator = new SettingsSectionCoordinator(this);

        PrintLayout = new PrintLayoutViewModel(settingsApi, printAssetApi);

        // Admin is added conditionally once AdminVm is wired and unlocked —
        // see SyncAdminSection. This keeps the sidebar from advertising admin
        // capabilities to an operator who doesn't have a session.
        // In the order the sidebar groups them: what every counter shares, then what is this PC's own.
        Sections = new ObservableCollection<SettingsSectionKey>
        {
            SettingsSectionKey.Connection,
            SettingsSectionKey.Invoice,
            SettingsSectionKey.PrintLayout,
            SettingsSectionKey.Ledgers,
            SettingsSectionKey.Masters,
            SettingsSectionKey.Server,
            SettingsSectionKey.Handover,
            SettingsSectionKey.Advanced,
        };
        SectionGroups = new ListCollectionView(Sections);
        SectionGroups.GroupDescriptions.Add(new PropertyGroupDescription(null, SettingsSectionGroupConverter.Instance));
        SelectedSection = SettingsSectionKey.Server;
        PrintLayout.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PrintLayoutViewModel.UpdatedAtUtc))
            {
                RaiseSectionHeader();
            }
        };

        Preview = previewFactory is not null
            ? previewFactory(Draft, PrintLayout, this)
            : new SettingsPreviewViewModel(Draft, PrintLayout, this, printDispatcher, printAssetApi, printPreferences);
        _sectionCoordinator.UpdatePreviewActivation();

        DensityOptions = UiDensityManager.All;
        UiDensity = UiDensityManager.CurrentDensity;
    }

    public IReadOnlyList<string> DensityOptions { get; }

    /// <summary>
    /// Settable by the shell (MainWindowViewModel) so the Admin section of
    /// Settings can bind to the same AdminUnlockViewModel instance the unlock
    /// dialog uses. Until it is set there is no Admin section.
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
            _sectionCoordinator.SyncAdminSection(_adminVm);
        }
    }
    private AdminUnlockViewModel? _adminVm;

    private void OnAdminVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AdminUnlockViewModel.IsUnlocked) or nameof(AdminUnlockViewModel.IsLocked))
        {
            _sectionCoordinator.SyncAdminSection(_adminVm);
        }
    }

    public string LogFolderPath => SettingsFolderLauncher.LogFolderPath;
    public string InstallFolderPath => AppContext.BaseDirectory;
    public string AppDataFolderPath => SettingsFolderLauncher.AppDataFolderPath;

    [RelayCommand]
    private static void OpenLogFolder() => SettingsFolderLauncher.OpenLogFolder();

    [RelayCommand]
    private static void OpenInstallFolder() => SettingsFolderLauncher.OpenInstallFolder();

    [RelayCommand]
    private static void OpenAppDataFolder() => SettingsFolderLauncher.OpenAppDataFolder();

    public ObservableCollection<SettingsSectionKey> Sections { get; }

    /// <summary>The sections under their group headings, for the sidebar.</summary>
    public ICollectionView SectionGroups { get; }
    public ServerSettingsViewModel Server { get; }

    /// <summary>Settings → Hand-over: this PC's hand-over key.</summary>
    public HandoverSettingsViewModel Handover { get; }
    public MasterDataSettingsViewModel MasterData { get; }
    public SettingsDraft Draft { get; }
    public PrintLayoutViewModel PrintLayout { get; }
    public SettingsPreviewViewModel Preview { get; }

    [ObservableProperty] private SettingsSectionKey selectedSection;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(BeginEditCommand), nameof(DiscardChangesCommand), nameof(SaveAllCommand))]
    private bool isLoading;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(BeginEditCommand), nameof(DiscardChangesCommand), nameof(SaveAllCommand))]
    private bool isSaving;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(BeginEditCommand), nameof(DiscardChangesCommand), nameof(SaveAllCommand))]
    private bool isEditing;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveAllCommand))]
    private bool isDirty;

    [ObservableProperty] private string statusMessage = string.Empty;
    [ObservableProperty] private string settingsSource = "—";
    [ObservableProperty] private string summary = string.Empty;
    [ObservableProperty] private DateTimeOffset? updatedAtUtc;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveCompanyName))]
    [NotifyCanExecuteChangedFor(nameof(BeginEditCommand))]
    private EffectiveCloudSettingsDto? settings;

    /// <summary>
    /// The items and karats of <see cref="Settings"/>, which is what was last loaded or saved.
    /// The Invoice tab, the batch scheduler and a hand-over read these, never the
    /// <see cref="Draft"/>'s rows, so an edit reaches a bill only once it is saved.
    /// </summary>
    [ObservableProperty] private MasterCatalog catalog = MasterCatalog.Empty;

    [ObservableProperty] private string uiDensity = UiDensityManager.Standard;

    public IReadOnlyList<string> CloudOwnedCategories { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<string> LocalOnlyCategories { get; private set; } = Array.Empty<string>();

    public bool IsConnectionVisible => _sectionCoordinator.IsConnectionVisible;
    public bool IsServerVisible => _sectionCoordinator.IsServerVisible;
    public bool IsInvoiceVisible => _sectionCoordinator.IsInvoiceVisible;
    public bool IsPrintLayoutVisible => _sectionCoordinator.IsPrintLayoutVisible;
    public bool IsLedgersVisible => _sectionCoordinator.IsLedgersVisible;
    public bool IsMastersVisible => _sectionCoordinator.IsMastersVisible;
    public bool IsAdvancedVisible => _sectionCoordinator.IsAdvancedVisible;
    public bool IsHandoverVisible => _sectionCoordinator.IsHandoverVisible;
    public bool IsAdminVisible => _sectionCoordinator.IsAdminVisible;

    /// <summary>Show the live preview pane only for Invoice and Print Layout sections.</summary>
    public bool IsPreviewVisible => _sectionCoordinator.IsPreviewVisible;

    public string ActiveCompanyName => Settings?.Connection.ActiveCompanyName ?? string.Empty;

    partial void OnSelectedSectionChanged(SettingsSectionKey value)
    {
        _sectionCoordinator.OnSelectedSectionChanged();
        RaiseSectionHeader();
    }

    // ---- The heading of the open section: its title, who it belongs to, and what can be done with it.

    public string SelectedSectionTitle => SettingsSections.TitleOf(SelectedSection);

    public SettingsSectionScope SelectedSectionScope => SettingsSections.ScopeOf(SelectedSection);

    public string SelectedSectionChipText => IsDirtyDraftSection ? "Unsaved changes" : SettingsSections.ChipTextOf(SelectedSectionScope);

    /// <summary>The chip turns amber with "Unsaved changes" while a draft section has edits not yet saved.</summary>
    public bool IsDirtyDraftSection => IsDirty && SettingsSections.IsEditedAsDraft(SelectedSection);

    /// <summary>The sections of the shared group have a band under the title, with the section's buttons in it.</summary>
    public bool HasHeaderBand => SelectedSectionScope == SettingsSectionScope.Shared;

    /// <summary>Refresh and Edit, then Discard and Save all: for the sections edited as one draft.</summary>
    public bool ShowsDraftButtons => SettingsSections.IsEditedAsDraft(SelectedSection);

    /// <summary>The band turns pale indigo while its section is being edited.</summary>
    public bool IsEditingBand => IsEditing && ShowsDraftButtons;

    public string SelectedSectionSubtitle
    {
        get
        {
            if (SelectedSection == SettingsSectionKey.PrintLayout)
            {
                return $"Used by the preview, printing and PDF export · Updated {FormatUpdated(PrintLayout.UpdatedAtUtc)}";
            }

            if (!ShowsDraftButtons)
            {
                return string.Empty;
            }

            var shared = IsDirtyDraftSection ? $"{SettingsSections.ChipTextOf(SettingsSectionScope.Shared)} · " : string.Empty;
            var state = IsEditing ? "Editing" : $"Source: {SettingsSource}";
            return $"{shared}{state} · Updated {FormatUpdated(UpdatedAtUtc)}";
        }
    }

    private static string FormatUpdated(DateTimeOffset? at)
        => at is { } value
            ? value.ToLocalTime().ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture)
            : "—";

    private void RaiseSectionHeader()
    {
        OnPropertyChanged(nameof(SelectedSectionTitle));
        OnPropertyChanged(nameof(SelectedSectionScope));
        OnPropertyChanged(nameof(SelectedSectionChipText));
        OnPropertyChanged(nameof(IsDirtyDraftSection));
        OnPropertyChanged(nameof(HasHeaderBand));
        OnPropertyChanged(nameof(ShowsDraftButtons));
        OnPropertyChanged(nameof(IsEditingBand));
        OnPropertyChanged(nameof(SelectedSectionSubtitle));
    }

    partial void OnIsEditingChanged(bool value)
    {
        MasterData.NotifyShellStateChanged();
        RaiseSectionHeader();
    }

    partial void OnIsDirtyChanged(bool value) => RaiseSectionHeader();

    partial void OnSettingsSourceChanged(string value) => RaiseSectionHeader();
    partial void OnUpdatedAtUtcChanged(DateTimeOffset? value) => RaiseSectionHeader();
    partial void OnUiDensityChanged(string value) => UiDensityManager.ApplyDensity(value);

    partial void OnSettingsChanged(EffectiveCloudSettingsDto? value)
    {
        // A visit to the Settings tab loads again; a load that brought the same
        // items and karats publishes nothing, so an open bill is left alone.
        var loaded = MasterCatalog.From(value?.Masters);
        if (!loaded.HasSameEntriesAs(Catalog))
        {
            Catalog = loaded;
        }

        MasterData.NotifyShellStateChanged();
    }

    public void NotifySectionPropertiesChanged()
    {
        OnPropertyChanged(nameof(IsConnectionVisible));
        OnPropertyChanged(nameof(IsServerVisible));
        OnPropertyChanged(nameof(IsInvoiceVisible));
        OnPropertyChanged(nameof(IsPrintLayoutVisible));
        OnPropertyChanged(nameof(IsLedgersVisible));
        OnPropertyChanged(nameof(IsMastersVisible));
        OnPropertyChanged(nameof(IsAdvancedVisible));
        OnPropertyChanged(nameof(IsHandoverVisible));
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

    /// <summary>
    /// Called each time the Settings tab is shown: loads what the server has
    /// now, unless an edit is in progress. A load replaces the draft, so it
    /// used to throw away, without a word, whatever had been typed and not
    /// yet saved when the operator looked at another tab and came back.
    /// </summary>
    public Task RefreshOnShownAsync() => IsEditing ? Task.CompletedTask : LoadAsync();

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
        catch (Exception ex)
        {
            StatusMessage = $"Load failed: {ApiResponseReader.FormatError(ex)}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool CanRefresh => !IsLoading && !IsEditing;

    private bool CanBeginEdit => !IsEditing && !IsLoading && Settings is not null;

    private bool CanDiscardChanges => IsEditing && !IsSaving;

    private bool CanSaveAll => IsEditing && IsDirty && !IsSaving;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => LoadAsync(CancellationToken.None);

    [RelayCommand(CanExecute = nameof(CanBeginEdit))]
    private void BeginEdit()
        => _editWorkflow.BeginEdit();

    [RelayCommand(CanExecute = nameof(CanDiscardChanges))]
    private void DiscardChanges()
        => _editWorkflow.DiscardChanges();

    [RelayCommand(CanExecute = nameof(CanSaveAll))]
    private Task SaveAllAsync(CancellationToken cancellationToken)
        => _editWorkflow.SaveAsync(cancellationToken);
}

public enum SettingsSectionKey
{
    Server,
    Connection,
    Invoice,
    PrintLayout,
    Ledgers,
    Masters,
    Advanced,
    Handover,
    Admin,
}
