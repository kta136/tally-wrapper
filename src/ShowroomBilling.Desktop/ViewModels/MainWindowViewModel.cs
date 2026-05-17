using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Options;
using ShowroomBilling.Desktop.Configuration;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.Shell;
using ShowroomBilling.Desktop.ViewModels.Admin;
using ShowroomBilling.Desktop.ViewModels.Bills;
using ShowroomBilling.Desktop.ViewModels.Invoice;
using ShowroomBilling.Desktop.ViewModels.Printing;
using ShowroomBilling.Desktop.ViewModels.Settings;
using ShowroomBilling.Desktop.ViewModels.Setup;
using ShowroomBilling.Desktop.ViewModels.SyntheticBatch;
using ShowroomBilling.Desktop.Views.Bills;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Masters;
using ShowroomBilling.Contracts.Settings;

namespace ShowroomBilling.Desktop.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IShellHealthHost, IShellPrintHost
{
    private static readonly TimeSpan HealthPollInterval = TimeSpan.FromSeconds(15);

    private readonly IRuntimeApiClient _runtimeApiClient;
    private readonly ISettingsApiClient _settingsApiClient;
    private readonly IBillsApiClient _billsApiClient;
    private readonly DesktopBootstrapOptions _bootstrapOptions;

    private readonly AdminTokenStore _adminTokenStore;
    private readonly ShellDialogCoordinator _dialogCoordinator;
    private readonly ShellHealthCoordinator _healthCoordinator;
    private readonly ShellPrintCoordinator _printCoordinator;

    private DispatcherTimer? _healthTimer;
    private bool _databaseConfigurationAttentionRequired;
    private bool _suppressSettingsRefresh;

    bool IShellHealthHost.DatabaseConfigurationAttentionRequired
    {
        get => _databaseConfigurationAttentionRequired;
        set => _databaseConfigurationAttentionRequired = value;
    }

    public MainWindowViewModel(
        IRuntimeApiClient runtimeApiClient,
        ISettingsApiClient settingsApiClient,
        IHealthApiClient healthApiClient,
        IMastersApiClient mastersApiClient,
        IBillsApiClient billsApiClient,
        IOptions<DesktopBootstrapOptions> bootstrapOptions,
        IOptions<DesktopLocalPreferencesOptions> localPreferencesOptions,
        ICompanyProfileProvider companyProfileProvider,
        IPrintLayoutOptionsProvider printLayoutOptionsProvider,
        IPrintDispatcher printDispatcher,
        IPrintPreferencesStore printPreferences,
        AdminTokenStore adminTokenStore,
        InvoiceViewModel invoice,
        BillsViewModel bills,
        BillDetailsViewModel billDetails,
        PrintPreviewViewModel printPreview,
        SettingsViewModel settings,
        AdminUnlockViewModel admin,
        SetupWizardViewModel setupWizard,
        SyntheticBatchViewModel syntheticBatch)
    {
        _runtimeApiClient = runtimeApiClient;
        _settingsApiClient = settingsApiClient;
        _billsApiClient = billsApiClient;
        _bootstrapOptions = bootstrapOptions.Value;
        _adminTokenStore = adminTokenStore;
        _adminTokenStore.Changed += OnAdminTokenChanged;
        _ = localPreferencesOptions;
        Invoice = invoice;
        Bills = bills;
        BillDetails = billDetails;
        PrintPreview = printPreview;
        Settings = settings;
        Admin = admin;
        SetupWizard = setupWizard;
        SyntheticBatch = syntheticBatch;
        ChangeNumberDialog = new ChangeNumberDialogViewModel();
        ReasonPromptDialog = new ReasonPromptDialogViewModel();
        _dialogCoordinator = new ShellDialogCoordinator(
            _adminTokenStore,
            Admin,
            ChangeNumberDialog,
            ReasonPromptDialog,
            value => ActiveDialog = value,
            () => ActiveDialog);
        ChangeNumberDialog.Closed += OnChangeNumberDialogClosed;
        ReasonPromptDialog.Closed += OnReasonPromptDialogClosed;
        BillDetails.BillMutated = () => _ = Bills.LoadAsync();

        TitleBar = new TitleBarViewModel
        {
            Company = "—",
            OperatorName = Environment.UserName,
        };
        Health = new HealthClusterViewModel();
        StatusBar = new StatusBarViewModel();
        FKeys = new FKeyStripViewModel();
        _healthCoordinator = new ShellHealthCoordinator(healthApiClient, mastersApiClient, this);
        _printCoordinator = new ShellPrintCoordinator(
            _billsApiClient,
            companyProfileProvider,
            printLayoutOptionsProvider,
            printDispatcher,
            printPreferences,
            this);

        ApplySystemState(SystemState.Degraded);

        SwitchTabCommand = new RelayCommand<NavTab>(SwitchTab);
        OpenShortcutsCommand = new RelayCommand(() => ActiveDialog = "shortcuts");
        OpenAdminUnlockCommand = new RelayCommand(() =>
        {
            ActiveDialog = "admin";
            _ = Admin.LoadStatusCommand.ExecuteAsync(null);
        });
        OpenSyntheticBatchCommand = new RelayCommand(() =>
        {
            SyntheticBatch.RefreshKaratOptions();
            ActiveDialog = "syntheticBatch";
        });
        OpenHealthCommand = new RelayCommand(() =>
        {
            ActiveDialog = "health";
            _ = RefreshHealthAsync(forceTallyCompany: true);
        });
        OpenDatabaseSettingsCommand = new RelayCommand(() =>
        {
            OpenDatabaseSettings();
        });
        RefreshHealthCommand = new AsyncRelayCommand(ct => RefreshHealthAsync(forceTallyCompany: true, ct));
        RefreshAllMastersCommand = new AsyncRelayCommand(RefreshAllMastersAsync);
        CloseDialogCommand = new RelayCommand(() =>
        {
            // Esc-close: if a prompting overlay is in flight, resolve its TCS
            // as "cancel" first so the await'ing caller unblocks instead of
            // hanging forever.
            if (ActiveDialog == "changeNumber") OnChangeNumberDialogClosed((false, string.Empty, null));
            else if (ActiveDialog == "reasonPrompt") OnReasonPromptDialogClosed(null);
            ActiveDialog = null;
        });
        OpenBillDetailsCommand = new RelayCommand(OpenSelectedBillDetails, () => Bills.SelectedBill is not null);
        OpenInvoicePreviewCommand = new AsyncRelayCommand(OpenInvoicePreviewAsync);
        OpenSelectedBillsPrintPreviewCommand = new AsyncRelayCommand(OpenSelectedBillsPrintPreviewAsync, () => Bills.CanPrintSelected);
        OpenFinalPrintPreviewCommand = new AsyncRelayCommand(OpenFinalPrintPreviewAsync, () => BillDetails.Bill is not null);
        Bills.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BillsViewModel.SelectedBill))
                OpenBillDetailsCommand.NotifyCanExecuteChanged();
            if (e.PropertyName is nameof(BillsViewModel.CanPrintSelected) or nameof(BillsViewModel.SelectedBillIds) or nameof(BillsViewModel.HasSelection))
                OpenSelectedBillsPrintPreviewCommand.NotifyCanExecuteChanged();
        };
        BillDetails.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BillDetailsViewModel.Bill))
                OpenFinalPrintPreviewCommand.NotifyCanExecuteChanged();
        };
        Invoice.SaveCompletedReadyForPrint += OnInvoiceSaveCompletedReadyForPrint;
        Invoice.PropertyChanged += OnInvoicePropertyChangedForStatusBar;
        StatusBar.ApplyRate24Kt(Invoice.Rate24Kt, Invoice.KaratMasters);
        StatusBar.ApplyLineCount(Invoice.ItemCount);
        StatusBar.ApplyLastSaved(Invoice.LastSavedAtUtc);
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.Settings) && Settings.Settings is { } saved)
            {
                _printCoordinator.ApplyPrintSettings(saved.Print, saved.Masters);
                if (!_suppressSettingsRefresh)
                    _ = _printCoordinator.RefreshPrintLayoutAsync();
                // Karat-master purity rows may have shifted — re-derive the
                // 22/18kt status-bar displays against the live 24kt rate.
                StatusBar.ApplyRate24Kt(Invoice.Rate24Kt, Invoice.KaratMasters);
            }
        };
        Settings.PrintLayout.PropertyChanged += (_, e) =>
        {
            // UpdatedAtUtc fires on successful load/save of the print-layout pane.
            if (e.PropertyName == nameof(PrintLayoutViewModel.UpdatedAtUtc))
                _ = _printCoordinator.RefreshPrintLayoutAsync();
        };

        Bills.AdminUnlockHandler = RequestAdminUnlockAsync;
        Settings.AdminUnlockHandler = RequestAdminUnlockAsync;
        Settings.AdminVm = Admin;
        SetupWizard.Completed += OnSetupWizardCompleted;
        SyntheticBatch.AdminUnlockHandler = RequestAdminUnlockAsync;
        SyntheticBatch.BatchCompleted += (_, _) => _ = Bills.LoadAsync();
        Bills.EditBillHandler = LoadBillForEditInInvoiceTabAsync;
        Bills.ChangeNumberPromptHandler = PromptChangeNumberAsync;
        Bills.ReasonPromptHandler = PromptReasonAsync;
    }

    private Task RequestAdminUnlockAsync(CancellationToken cancellationToken)
    {
        return _dialogCoordinator.RequestAdminUnlockAsync(cancellationToken);
    }

    private void OnAdminTokenChanged(Contracts.Admin.AdminUnlockResponse? unlock)
    {
        _dialogCoordinator.OnAdminTokenChanged(unlock);
    }

    private async Task LoadBillForEditInInvoiceTabAsync(Guid billId, CancellationToken cancellationToken)
    {
        ActiveTab = NavTab.Invoice;
        await Invoice.LoadBillForEditAsync(billId, cancellationToken);
    }

    private async Task<(bool Confirmed, string NewNumber, string? Reason)> PromptChangeNumberAsync(
        BillListRowViewModel row,
        CancellationToken cancellationToken)
    {
        var token = _adminTokenStore.Current?.Token;
        if (string.IsNullOrWhiteSpace(token)) return (false, string.Empty, null);

        var numbering = Settings.Settings?.Numbering;
        var fiscalYear = Contracts.Numbering.InvoiceNumberFormatter.ComputeFiscalYear(DateTimeOffset.UtcNow);

        var result = await ShowChangeNumberDialogAsync(
            row.InvoiceNumber,
            numbering?.InvoicePrefix,
            numbering?.InvoiceSuffix,
            fiscalYear,
            numbering?.InvoicePadding ?? Contracts.Numbering.InvoiceNumberFormatter.DefaultPadding);
        if (!result.Confirmed) return (false, string.Empty, null);

        // Dry-run to surface warnings before the real commit.
        try
        {
            var dry = await _billsApiClient.ChangeInvoiceNumberAsync(
                row.Id,
                new ChangeBillNumberRequest(result.NewNumber, result.Reason, DryRun: true),
                token,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(dry.WarningSummary))
            {
                var confirm = System.Windows.MessageBox.Show(
                    dry.WarningSummary,
                    "Invoice-number change warnings",
                    System.Windows.MessageBoxButton.OKCancel,
                    System.Windows.MessageBoxImage.Warning,
                    System.Windows.MessageBoxResult.Cancel);
                if (confirm != System.Windows.MessageBoxResult.OK) return (false, string.Empty, null);
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Validation failed: {ex.Message}",
                "Change invoice number",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            return (false, string.Empty, null);
        }

        return (true, result.NewNumber, result.Reason);
    }

    private Task<(bool Confirmed, string NewNumber, string? Reason)> ShowChangeNumberDialogAsync(
        string? currentNumber, string? prefix, string? suffix, string fiscalYear, int padding)
    {
        return _dialogCoordinator.ShowChangeNumberDialogAsync(currentNumber, prefix, suffix, fiscalYear, padding);
    }

    private void OnChangeNumberDialogClosed((bool Confirmed, string NewNumber, string? Reason) result)
    {
        _dialogCoordinator.OnChangeNumberDialogClosed(result);
    }

    private Task<string?> PromptReasonAsync(string title, string message, CancellationToken cancellationToken)
    {
        return _dialogCoordinator.PromptReasonAsync(title, message, cancellationToken);
    }

    private void OnReasonPromptDialogClosed(string? result)
    {
        _dialogCoordinator.OnReasonPromptDialogClosed(result);
    }

    private async void OnInvoiceSaveCompletedReadyForPrint(object? sender, Guid billId)
    {
        await _printCoordinator.HandleInvoiceSaveCompletedAsync();
    }

    private void OnInvoicePropertyChangedForStatusBar(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(InvoiceViewModel.Rate24Kt):
                StatusBar.ApplyRate24Kt(Invoice.Rate24Kt, Invoice.KaratMasters);
                break;
            case nameof(InvoiceViewModel.ItemCount):
                StatusBar.ApplyLineCount(Invoice.ItemCount);
                break;
            case nameof(InvoiceViewModel.LastSavedAtUtc):
                StatusBar.ApplyLastSaved(Invoice.LastSavedAtUtc);
                break;
        }
    }

    public InvoiceViewModel Invoice { get; }
    public BillsViewModel Bills { get; }
    public BillDetailsViewModel BillDetails { get; }
    public PrintPreviewViewModel PrintPreview { get; }
    public SettingsViewModel Settings { get; }
    public AdminUnlockViewModel Admin { get; }
    public SetupWizardViewModel SetupWizard { get; }
    public SyntheticBatchViewModel SyntheticBatch { get; }
    public ChangeNumberDialogViewModel ChangeNumberDialog { get; }
    public ReasonPromptDialogViewModel ReasonPromptDialog { get; }

    public TitleBarViewModel TitleBar { get; }
    public HealthClusterViewModel Health { get; }
    public StatusBarViewModel StatusBar { get; }
    public FKeyStripViewModel FKeys { get; }

    public IRelayCommand<NavTab> SwitchTabCommand { get; }
    public IRelayCommand OpenShortcutsCommand { get; }
    public IRelayCommand OpenAdminUnlockCommand { get; }
    public IRelayCommand OpenSyntheticBatchCommand { get; }
    public IRelayCommand OpenHealthCommand { get; }
    public IRelayCommand OpenDatabaseSettingsCommand { get; }
    public IAsyncRelayCommand RefreshHealthCommand { get; }
    public IAsyncRelayCommand RefreshAllMastersCommand { get; }
    public IRelayCommand CloseDialogCommand { get; }
    public IRelayCommand OpenBillDetailsCommand { get; }
    public IAsyncRelayCommand OpenInvoicePreviewCommand { get; }
    public IAsyncRelayCommand OpenSelectedBillsPrintPreviewCommand { get; }
    public IAsyncRelayCommand OpenFinalPrintPreviewCommand { get; }

    [ObservableProperty]
    private NavTab activeTab = NavTab.Invoice;

    [ObservableProperty]
    private SystemState systemState = SystemState.Degraded;

    [ObservableProperty]
    private string? activeDialog;

    [ObservableProperty]
    private string bannerText = string.Empty;

    [ObservableProperty]
    private SystemHealthSnapshot? lastHealthSnapshot;

    [ObservableProperty]
    private DateTimeOffset? lastHealthCheckedAtUtc;

    [ObservableProperty]
    private bool isHealthRefreshing;

    [ObservableProperty]
    private string? mastersRefreshMessage;

    [ObservableProperty]
    private bool isRefreshingAllMasters;

    public string LastHealthCheckedDisplay =>
        LastHealthCheckedAtUtc is null
            ? "never"
            : LastHealthCheckedAtUtc.Value.ToLocalTime().ToString("HH:mm:ss");

    partial void OnLastHealthCheckedAtUtcChanged(DateTimeOffset? value) =>
        OnPropertyChanged(nameof(LastHealthCheckedDisplay));

    public bool IsInvoiceVisible => ActiveTab == NavTab.Invoice && SystemState != SystemState.Limited;
    public bool IsBillsVisible => ActiveTab == NavTab.Bills && SystemState != SystemState.Limited;
    public bool IsSettingsVisible => ActiveTab == NavTab.Settings;
    public bool IsLimitedVisible => SystemState == SystemState.Limited && ActiveTab != NavTab.Settings;

    public bool IsDegradedBannerVisible => SystemState == SystemState.Degraded;
    public bool IsLimitedBannerVisible => SystemState == SystemState.Limited;

    partial void OnActiveTabChanged(NavTab value)
    {
        FKeys.SetTab(value);
        OnPropertyChanged(nameof(IsInvoiceVisible));
        OnPropertyChanged(nameof(IsBillsVisible));
        OnPropertyChanged(nameof(IsSettingsVisible));
        if (value == NavTab.Bills) _ = Bills.LoadAsync();
        // waitForApi: true defers the preview fetch until the API child has
        // bound its port. On a cold launch, the very first activation can fire
        // before that — without the wait the catch in RefreshNextNumberAsync
        // silently swallows the failure and the operator pays the full
        // cold-path cost on first SaveDraft instead.
        if (value == NavTab.Invoice) _ = Invoice.RefreshNextNumberAsync(waitForApi: true);
        if (value == NavTab.Settings) _ = Settings.LoadAsync();
    }

    partial void OnActiveDialogChanged(string? value)
    {
        _dialogCoordinator.OnActiveDialogChanged(value);
        _printCoordinator.OnActiveDialogChanged(value);
    }

    partial void OnSystemStateChanged(SystemState value) => ApplySystemState(value);

    private void ApplySystemState(SystemState value)
    {
        _healthCoordinator.ApplySystemState(value);
        OnPropertyChanged(nameof(IsInvoiceVisible));
        OnPropertyChanged(nameof(IsBillsVisible));
        OnPropertyChanged(nameof(IsSettingsVisible));
        OnPropertyChanged(nameof(IsLimitedVisible));
        OnPropertyChanged(nameof(IsDegradedBannerVisible));
        OnPropertyChanged(nameof(IsLimitedBannerVisible));
    }

    public void SwitchTab(NavTab tab) => ActiveTab = tab;

    private void OpenSelectedBillDetails()
    {
        var selected = Bills.SelectedBill;
        if (selected is null) return;
        ActiveDialog = "billDetails";
        _ = BillDetails.LoadAsync(selected.Id);
    }

    private async Task OpenInvoicePreviewAsync(CancellationToken cancellationToken)
    {
        await _printCoordinator.OpenInvoicePreviewAsync(cancellationToken);
    }

    private async Task OpenFinalPrintPreviewAsync(CancellationToken cancellationToken)
    {
        await _printCoordinator.OpenFinalPrintPreviewAsync(cancellationToken);
    }

    private async Task OpenSelectedBillsPrintPreviewAsync(CancellationToken cancellationToken)
    {
        await _printCoordinator.OpenSelectedBillsPrintPreviewAsync(cancellationToken);
    }

    public async Task InitializeAsync()
    {
        var bootstrapTask = ApplyBootstrapAsync();
        var settingsTask = ApplySettingsAsync();
        var healthTask = RefreshHealthAsync(forceTallyCompany: true);

        await Task.WhenAll(bootstrapTask, settingsTask, healthTask);
        var settingsResponse = await settingsTask;

        _ = _printCoordinator.WarmUpPrintPipelineAsync();
        // Invoice is the default tab and OnActiveTabChanged does NOT fire for
        // the constructor's same-value assignment, so the preview fetch
        // wouldn't otherwise run on a cold launch where the operator stays put.
        // Triggering it explicitly here primes the numbering preview + warms
        // the connection pool while the user is still settling into the form.
        if (ActiveTab == NavTab.Invoice) _ = Invoice.RefreshNextNumberAsync(waitForApi: true);

        if (LastHealthSnapshot?.ApiReachable == true
            && await SetupWizard.PrepareForStartupAsync(LastHealthSnapshot, settingsResponse))
        {
            ActiveDialog = "setupWizard";
        }
        else if (_databaseConfigurationAttentionRequired && LastHealthSnapshot?.ApiReachable == true)
        {
            OpenDatabaseSettings();
        }
        StartHealthPolling();
    }

    private async Task ApplyBootstrapAsync()
    {
        try
        {
            var bootstrap = await _runtimeApiClient.GetBootstrapAsync();
            StatusBar.ApplyDatabaseIdentity(bootstrap.DatabaseIdentity, bootstrap.EnvironmentName);
        }
        catch
        {
            // Health polling will classify a fully unreachable API as Limited.
            StatusBar.ApplyDatabaseIdentity(null, null);
        }
    }

    private async Task<EffectiveSettingsResponse?> ApplySettingsAsync()
    {
        try
        {
            var response = await _settingsApiClient.GetEffectiveSettingsAsync();
            _suppressSettingsRefresh = true;
            try
            {
                Settings.ApplyEffectiveSettings(response);
            }
            finally
            {
                _suppressSettingsRefresh = false;
            }

            TitleBar.Company = response.Settings.Connection.ActiveCompanyName;
            _printCoordinator.ApplyPrintSettings(response.Settings.Print, response.Settings.Masters);

            await _printCoordinator.RefreshPrintLayoutAsync();

            _ = Invoice.RefreshNextNumberAsync();
            return response;
        }
        catch
        {
            _databaseConfigurationAttentionRequired = true;
            return null;
        }
    }

    private async void OnSetupWizardCompleted(object? sender, EventArgs e)
    {
        ActiveDialog = null;
        _databaseConfigurationAttentionRequired = false;
        await ApplySettingsAsync();
        await RefreshHealthAsync(forceTallyCompany: true);
    }

    private void OpenDatabaseSettings()
    {
        Settings.SelectedSection = SettingsSectionKey.Database;
        ActiveTab = NavTab.Settings;
        _ = Settings.LoadDatabaseConfigAsync();
    }

    private void StartHealthPolling()
    {
        if (_healthTimer is not null) return;
        _healthTimer = new DispatcherTimer { Interval = HealthPollInterval };
        _healthTimer.Tick += async (_, _) => await RefreshHealthAsync();
        _healthTimer.Start();
    }

    private async Task RefreshAllMastersAsync()
        => await _healthCoordinator.RefreshAllMastersAsync();

    public async Task RefreshHealthAsync(
        bool forceTallyCompany = false,
        CancellationToken cancellationToken = default)
        => await _healthCoordinator.RefreshHealthAsync(forceTallyCompany, cancellationToken);
}
