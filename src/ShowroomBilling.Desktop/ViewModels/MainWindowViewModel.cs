using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Options;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Masters;
using ShowroomBilling.Contracts.Settings;
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

namespace ShowroomBilling.Desktop.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IShellHealthHost, IShellPrintHost
{
    private static readonly TimeSpan HealthPollInterval = TimeSpan.FromSeconds(15);

    private readonly ISettingsApiClient _settingsApiClient;
    private readonly IBillsApiClient _billsApiClient;
    private readonly DesktopBootstrapOptions _bootstrapOptions;

    private readonly AdminTokenStore _adminTokenStore;
    private readonly ShellDialogCoordinator _dialogCoordinator;
    private readonly ShellHealthCoordinator _healthCoordinator;
    private readonly ShellPrintCoordinator _printCoordinator;

    private DispatcherTimer? _healthTimer;
    private bool _suppressSettingsRefresh;

    public MainWindowViewModel(
        ISettingsApiClient settingsApiClient,
        IHealthApiClient healthApiClient,
        IMastersApiClient mastersApiClient,
        IBillsApiClient billsApiClient,
        IOptions<DesktopBootstrapOptions> bootstrapOptions,
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
        _settingsApiClient = settingsApiClient;
        _billsApiClient = billsApiClient;
        _bootstrapOptions = bootstrapOptions.Value;
        _adminTokenStore = adminTokenStore;
        _adminTokenStore.Changed += OnAdminTokenChanged;
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
        ConfirmDialog = new ConfirmDialogViewModel();
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
        DismissBannerCommand = new RelayCommand(() =>
        {
            _dismissedBannerText = BannerText;
            RaiseBannerChanged();
        });
        OpenShortcutsCommand = new RelayCommand(() => ActiveDialog = ShellDialogs.Shortcuts);
        OpenAdminUnlockCommand = new RelayCommand(() =>
        {
            ActiveDialog = ShellDialogs.Admin;
            _ = Admin.LoadStatusCommand.ExecuteAsync(null);
        });
        // Bare-key variants for the window key bindings. A false CanExecute
        // lets the key fall through, so "?" and "`" still type into text
        // boxes, and an open (possibly awaited) prompt is never replaced.
        OpenShortcutsKeyCommand = new RelayCommand(() => OpenShortcutsCommand.Execute(null), CanUseBareKeyShortcut);
        OpenAdminUnlockKeyCommand = new RelayCommand(() => OpenAdminUnlockCommand.Execute(null), CanUseBareKeyShortcut);
        OpenSyntheticBatchCommand = new RelayCommand(() =>
        {
            SyntheticBatch.RefreshKaratOptions();
            if (SyntheticBatch.Rate24Kt <= 0m && Invoice.Rate24Kt is > 0m)
            {
                SyntheticBatch.Rate24Kt = Invoice.Rate24Kt.Value;
            }
            ActiveDialog = ShellDialogs.SyntheticBatch;
        });
        OpenHealthCommand = new RelayCommand(() =>
        {
            ActiveDialog = ShellDialogs.Health;
            _ = RefreshHealthAsync(forceTallyCompany: true);
        });
        SaveActiveCommand = new AsyncRelayCommand(SaveActiveAsync);
        AddInvoiceRowCommand = new RelayCommand(
            () => Invoice.AddRowCommand.Execute(null),
            () => ActiveTab == NavTab.Invoice && Invoice.AddRowCommand.CanExecute(null));
        RemoveInvoiceRowCommand = new RelayCommand(
            () => Invoice.RemoveFocusedRowCommand.Execute(null),
            () => ActiveTab == NavTab.Invoice && Invoice.RemoveFocusedRowCommand.CanExecute(null));
        PrintEstimateCommand = new RelayCommand(PrintEstimate, () => Invoice.ItemCount > 0);
        NewInvoiceBillCommand = new AsyncRelayCommand(NewInvoiceBillAsync);
        OpenServerSettingsCommand = new RelayCommand(OpenServerSettings);
        RefreshHealthCommand = new AsyncRelayCommand(ct => RefreshHealthAsync(forceTallyCompany: true, ct));
        RefreshAllMastersCommand = new AsyncRelayCommand(RefreshAllMastersAsync);
        CloseDialogCommand = new RelayCommand(() =>
        {
            // The confirm overlay sits above everything, so Esc answers it first.
            if (ConfirmDialog.IsOpen)
            {
                ConfirmDialog.Cancel();
                return;
            }

            // Esc-close: if a prompting overlay is in flight, resolve its TCS
            // as "cancel" first so the await'ing caller unblocks instead of
            // hanging forever.
            if (ActiveDialog == ShellDialogs.ChangeNumber) OnChangeNumberDialogClosed((false, string.Empty, null));
            else if (ActiveDialog == ShellDialogs.ReasonPrompt) OnReasonPromptDialogClosed(null);

            if (ActiveDialog is not null)
            {
                ActiveDialog = null;
                return;
            }

            if (ActiveTab == NavTab.Settings && Settings.DiscardChangesCommand.CanExecute(null))
            {
                Settings.DiscardChangesCommand.Execute(null);
            }
        });
        OpenBillDetailsCommand = new RelayCommand(OpenSelectedBillDetails, () => Bills.SelectedBill is not null);
        OpenSelectedBillsPrintPreviewCommand = new AsyncRelayCommand(OpenSelectedBillsPrintPreviewAsync, () => Bills.CanPrintSelected);
        OpenFinalPrintPreviewCommand = new RelayCommand(_printCoordinator.OpenFinalPrintPreview, () => BillDetails.Bill is not null);
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
        Invoice.RefreshRates();
        Invoice.PropertyChanged += (_, e) =>
        {
            // Printing an estimate needs at least one line on it.
            if (e.PropertyName == nameof(InvoiceViewModel.ItemCount))
                PrintEstimateCommand.NotifyCanExecuteChanged();
        };
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.Settings) && Settings.Settings is { } saved)
            {
                _printCoordinator.ApplyPrintSettings(saved.Print, saved.Masters);
                if (!_suppressSettingsRefresh)
                    _ = _printCoordinator.RefreshPrintLayoutAsync();
                // Karat-master purity rows may have shifted: re-derive the
                // 22K and 18K rates shown beside the line items.
                Invoice.RefreshRates();
            }
        };
        Settings.PrintLayout.PropertyChanged += (_, e) =>
        {
            // UpdatedAtUtc fires on successful load/save of the print-layout pane.
            if (e.PropertyName == nameof(PrintLayoutViewModel.UpdatedAtUtc))
                _ = _printCoordinator.RefreshPrintLayoutAsync();
        };

        Bills.AdminUnlockHandler = RequestAdminUnlockAsync;
        Invoice.AdminUnlockHandler = RequestAdminUnlockAsync;
        Settings.AdminVm = Admin;
        SetupWizard.Completed += OnSetupWizardCompleted;
        SyntheticBatch.AdminUnlockHandler = RequestAdminUnlockAsync;
        SyntheticBatch.BatchCompleted += (_, _) => _ = Bills.LoadAsync();
        Bills.EditBillHandler = LoadBillForEditInInvoiceTabAsync;
        Bills.ChangeNumberPromptHandler = PromptChangeNumberAsync;
        Bills.ReasonPromptHandler = PromptReasonAsync;
        Bills.ConfirmHandler = ConfirmDialog.ShowAsync;
        Bills.OpenDetailsCommand = OpenBillDetailsCommand;
        Bills.PrintSelectedCommand = OpenSelectedBillsPrintPreviewCommand;
        BillDetails.ConfirmHandler = ConfirmDialog.ShowAsync;
        Invoice.ConfirmHandler = ConfirmDialog.ShowAsync;
        Settings.Server.ConfirmHandler = ConfirmDialog.ShowAsync;
        Bills.RefreshTallyHealthHandler = RefreshBillsTallyHealthAsync;
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
        // A bill keeps the fiscal year of its bill date, so the preview uses
        // that rather than today's; they differ for a bill from an earlier year.
        var fiscalYear = row.BillDate is { } billDate
            ? Contracts.Numbering.InvoiceNumberFormatter.ComputeFiscalYear(billDate)
            : Contracts.Numbering.InvoiceNumberFormatter.ComputeFiscalYear(DateTimeOffset.UtcNow);

        var result = await ShowChangeNumberDialogAsync(
            row.InvoiceNumber,
            numbering?.InvoicePrefix,
            numbering?.InvoiceSuffix,
            fiscalYear,
            numbering?.InvoicePadding ?? Contracts.Numbering.InvoiceNumberFormatter.DefaultPadding,
            $"{row.PartyName} · ₹{Shell.UiCulture.Rupees(row.GrandTotal)} · {row.StateChipLabel}");
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
                var goAhead = await ConfirmDialog.ShowAsync(new ConfirmRequest(
                    "Change the number anyway?",
                    dry.WarningSummary,
                    "Change number",
                    IsWarning: true));
                if (!goAhead) return (false, string.Empty, null);
            }
        }
        catch (Exception ex)
        {
            Bills.StatusMessage = $"Change number check failed: {ApiResponseReader.FormatError(ex)}";
            return (false, string.Empty, null);
        }

        return (true, result.NewNumber, result.Reason);
    }

    private Task<(bool Confirmed, string NewNumber, string? Reason)> ShowChangeNumberDialogAsync(
        string? currentNumber, string? prefix, string? suffix, string fiscalYear, int padding, string? context)
    {
        return _dialogCoordinator.ShowChangeNumberDialogAsync(currentNumber, prefix, suffix, fiscalYear, padding, context);
    }

    private void OnChangeNumberDialogClosed((bool Confirmed, string NewNumber, string? Reason) result)
    {
        _dialogCoordinator.OnChangeNumberDialogClosed(result);
    }

    private Task<string?> PromptReasonAsync(ReasonRequest request, CancellationToken cancellationToken)
    {
        return _dialogCoordinator.PromptReasonAsync(request, cancellationToken);
    }

    private void OnReasonPromptDialogClosed(string? result)
    {
        _dialogCoordinator.OnReasonPromptDialogClosed(result);
    }

    private async void OnInvoiceSaveCompletedReadyForPrint(object? sender, Guid billId)
    {
        try
        {
            await _printCoordinator.HandleInvoiceSaveCompletedAsync();
        }
        catch (Exception ex)
        {
            Invoice.SaveStatus = $"Bill saved, but print preparation failed: {ex.Message}";
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
    public ConfirmDialogViewModel ConfirmDialog { get; }

    public TitleBarViewModel TitleBar { get; }
    public HealthClusterViewModel Health { get; }
    public StatusBarViewModel StatusBar { get; }
    public FKeyStripViewModel FKeys { get; }

    public IRelayCommand<NavTab> SwitchTabCommand { get; }
    public IRelayCommand OpenShortcutsCommand { get; }
    public IRelayCommand OpenAdminUnlockCommand { get; }
    public IRelayCommand OpenShortcutsKeyCommand { get; }
    public IRelayCommand OpenAdminUnlockKeyCommand { get; }

    /// <summary>True while any overlay, including the confirm question, covers the tabs.</summary>
    public bool IsAnyDialogOpen => ActiveDialog is not null || ConfirmDialog.IsOpen;

    private bool CanUseBareKeyShortcut() =>
        !IsAnyDialogOpen
        && System.Windows.Input.Keyboard.FocusedElement is not (System.Windows.Controls.Primitives.TextBoxBase or System.Windows.Controls.PasswordBox);
    public IRelayCommand OpenSyntheticBatchCommand { get; }
    public IRelayCommand OpenHealthCommand { get; }
    public IRelayCommand OpenServerSettingsCommand { get; }
    public IAsyncRelayCommand RefreshHealthCommand { get; }
    public IAsyncRelayCommand RefreshAllMastersCommand { get; }
    public IAsyncRelayCommand SaveActiveCommand { get; }
    public IRelayCommand AddInvoiceRowCommand { get; }
    public IRelayCommand RemoveInvoiceRowCommand { get; }
    public IRelayCommand PrintEstimateCommand { get; }

    /// <summary>Ctrl+Shift+N: the Invoice tab's New bill. Ctrl+N is New row.</summary>
    public IAsyncRelayCommand NewInvoiceBillCommand { get; }
    public IRelayCommand CloseDialogCommand { get; }
    public IRelayCommand OpenBillDetailsCommand { get; }
    public IAsyncRelayCommand OpenSelectedBillsPrintPreviewCommand { get; }
    public IRelayCommand OpenFinalPrintPreviewCommand { get; }

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
    private string? mastersRefreshMessage;

    [ObservableProperty]
    private bool isRefreshingAllMasters;

    public string LastHealthCheckedDisplay =>
        LastHealthCheckedAtUtc is null
            ? "never"
            : LastHealthCheckedAtUtc.Value.ToLocalTime().ToString("HH:mm:ss");

    partial void OnLastHealthCheckedAtUtcChanged(DateTimeOffset? value) =>
        OnPropertyChanged(nameof(LastHealthCheckedDisplay));

    partial void OnLastHealthSnapshotChanged(SystemHealthSnapshot? value)
    {
        Bills.ApplyTallyHealthSnapshot(value);
        RaiseBannerChanged();
    }

    partial void OnBannerTextChanged(string value) => RaiseBannerChanged();

    private void RaiseBannerChanged()
    {
        OnPropertyChanged(nameof(BannerTone));
        OnPropertyChanged(nameof(IsBannerVisible));
        OnPropertyChanged(nameof(IsBannerDismissible));
    }

    public bool IsInvoiceVisible => ActiveTab == NavTab.Invoice && SystemState != SystemState.Limited;
    public bool IsBillsVisible => ActiveTab == NavTab.Bills && SystemState != SystemState.Limited;
    public bool IsSettingsVisible => ActiveTab == NavTab.Settings;
    public bool IsLimitedVisible => SystemState == SystemState.Limited && ActiveTab != NavTab.Settings;

    /// <summary>
    /// How the banner under the top bar reads: "info" for a note the operator may dismiss,
    /// "warn" for something to check, "err" when bills cannot be saved. Empty when there is none.
    /// </summary>
    public string BannerTone => SystemState switch
    {
        SystemState.Limited => "err",
        SystemState.Degraded when LastHealthSnapshot?.Runtime?.DatabaseIdentityMatches == false => "warn",
        SystemState.Degraded => "info",
        _ => string.Empty,
    };

    public bool IsBannerVisible =>
        !string.IsNullOrWhiteSpace(BannerText)
        && BannerTone.Length > 0
        && !(BannerTone == "info" && BannerText == _dismissedBannerText);

    public bool IsBannerDismissible => BannerTone == "info";

    /// <summary>Hides the information banner until its text changes.</summary>
    public IRelayCommand DismissBannerCommand { get; }

    private string? _dismissedBannerText;

    partial void OnActiveTabChanged(NavTab value)
    {
        FKeys.SetScreen(value, SystemState == SystemState.Limited);
        AddInvoiceRowCommand.NotifyCanExecuteChanged();
        RemoveInvoiceRowCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsInvoiceVisible));
        OnPropertyChanged(nameof(IsBillsVisible));
        OnPropertyChanged(nameof(IsSettingsVisible));
        if (value == NavTab.Bills) _ = Bills.LoadAsync();
        // waitForApi: true defers the preview fetch until the API has been
        // probed. On a cold launch, the very first activation can fire
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
        FKeys.SetScreen(ActiveTab, value == SystemState.Limited);
        OnPropertyChanged(nameof(IsInvoiceVisible));
        OnPropertyChanged(nameof(IsBillsVisible));
        OnPropertyChanged(nameof(IsSettingsVisible));
        OnPropertyChanged(nameof(IsLimitedVisible));
        RaiseBannerChanged();
    }

    public void SwitchTab(NavTab tab) => ActiveTab = tab;

    private async Task SaveActiveAsync(CancellationToken cancellationToken)
    {
        if (IsAnyDialogOpen)
        {
            return;
        }

        if (ActiveTab == NavTab.Invoice && Invoice.SaveDraftCommand.CanExecute(null))
        {
            await Invoice.SaveDraftCommand.ExecuteAsync(null);
        }
        else if (ActiveTab == NavTab.Settings && Settings.SaveAllCommand.CanExecute(null))
        {
            await Settings.SaveAllCommand.ExecuteAsync(null);
        }
    }

    private void PrintEstimate()
    {
        if (!IsAnyDialogOpen && ActiveTab == NavTab.Invoice)
        {
            _printCoordinator.OpenInvoicePreview();
        }
    }

    private async Task NewInvoiceBillAsync()
    {
        if (IsAnyDialogOpen || !IsInvoiceVisible || !Invoice.NewBillCommand.CanExecute(null))
        {
            return;
        }

        await Invoice.NewBillCommand.ExecuteAsync(null);
    }

    private void OpenSelectedBillDetails()
    {
        var selected = Bills.SelectedBill;
        if (selected is null) return;
        ActiveDialog = ShellDialogs.BillDetails;
        _ = BillDetails.LoadAsync(selected.Id);
    }

    private async Task OpenSelectedBillsPrintPreviewAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _printCoordinator.OpenSelectedBillsPrintPreviewAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            Bills.StatusMessage = $"Print preview failed: {ex.Message}";
        }
    }

    public async Task InitializeAsync()
    {
        var settingsTask = ApplySettingsAsync();
        // The forced check also brings the database identity for the status bar.
        var healthTask = RefreshHealthAsync(forceTallyCompany: true);

        await Task.WhenAll(settingsTask, healthTask);
        var settingsResponse = await settingsTask;

        _ = _printCoordinator.WarmUpPrintPipelineAsync();
        // Invoice is the default tab and OnActiveTabChanged does NOT fire for
        // the constructor's same-value assignment, so the preview fetch
        // wouldn't otherwise run on a cold launch where the operator stays put.
        // Triggering it explicitly here primes the numbering preview + warms
        // the connection pool while the user is still settling into the form.
        if (ActiveTab == NavTab.Invoice) _ = Invoice.RefreshNextNumberAsync(waitForApi: true);

        if (LastHealthSnapshot?.ApiReachable == true
            && await SetupWizard.PrepareForStartupAsync(settingsResponse))
        {
            ActiveDialog = ShellDialogs.SetupWizard;
            _ = LoadSetupChoicesAsync();
        }
        StartHealthPolling();
    }

    /// <summary>Fills the companies, ledgers and voucher types the setup form offers from the server's stored Tally snapshots. The form still takes typing when there are none.</summary>
    private async Task LoadSetupChoicesAsync()
    {
        try
        {
            await Settings.MasterData.LoadMissingSnapshotsAsync(CancellationToken.None);
        }
        catch
        {
            // Best effort: the operator can type the names.
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
            return null;
        }
    }

    private async void OnSetupWizardCompleted(object? sender, EventArgs e)
    {
        try
        {
            ActiveDialog = null;
            await ApplySettingsAsync();
            await RefreshHealthAsync(forceTallyCompany: true);
        }
        catch (Exception ex)
        {
            BannerText = $"Setup completed, but refresh failed: {ex.Message}";
            SystemState = SystemState.Degraded;
        }
    }

    private void OpenServerSettings()
    {
        Settings.SelectedSection = SettingsSectionKey.Server;
        ActiveTab = NavTab.Settings;
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

    private async Task<SystemHealthSnapshot?> RefreshBillsTallyHealthAsync(CancellationToken cancellationToken)
    {
        await RefreshHealthAsync(forceTallyCompany: true, cancellationToken);
        return LastHealthSnapshot;
    }
}
