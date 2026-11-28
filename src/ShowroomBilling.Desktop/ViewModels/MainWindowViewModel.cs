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
using ShowroomBilling.Desktop.ViewModels.Handover;
using ShowroomBilling.Desktop.ViewModels.Invoice;
using ShowroomBilling.Desktop.ViewModels.Printing;
using ShowroomBilling.Desktop.ViewModels.Settings;
using ShowroomBilling.Desktop.ViewModels.Setup;
using ShowroomBilling.Desktop.ViewModels.SyntheticBatch;
using ShowroomBilling.Desktop.Views.Bills;

namespace ShowroomBilling.Desktop.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IShellHealthHost, IShellPrintHost, IHandoverHost
{
    private static readonly TimeSpan HealthPollInterval = TimeSpan.FromSeconds(15);

    private readonly ISettingsApiClient _settingsApiClient;
    private readonly IBillsApiClient _billsApiClient;
    private readonly DesktopBootstrapOptions _bootstrapOptions;

    private readonly AdminTokenStore _adminTokenStore;
    private readonly DialogService _dialogs;
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
        DialogService dialogs,
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
        _dialogs = dialogs;
        Invoice = invoice;
        Bills = bills;
        BillDetails = billDetails;
        PrintPreview = printPreview;
        Settings = settings;
        Admin = admin;
        SetupWizard = setupWizard;
        SyntheticBatch = syntheticBatch;
        _dialogs.PropertyChanged += (_, e) =>
        {
            // The dialog service holds which dialog is open; the window binds it through here.
            if (e.PropertyName != nameof(DialogService.ActiveDialog)) return;
            OnPropertyChanged(nameof(ActiveDialog));
            Handover?.OnActiveDialogChanged(ActiveDialog);
        };
        BillDetails.BillMutated = () => _ = Bills.LoadAsync();

        TitleBar = new TitleBarViewModel
        {
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

        Settings.AdminVm = Admin;
        SetupWizard.Completed += OnSetupWizardCompleted;
        SyntheticBatch.BillsSaved += (_, _) => _ = Bills.LoadAsync();
        // "Open Bills tab" on a batch's result: the dialog closes and the bills it made are in front.
        SyntheticBatch.OpenBillsRequested += (_, _) =>
        {
            if (ActiveDialog == ShellDialogs.SyntheticBatch) ActiveDialog = null;
            ActiveTab = NavTab.Bills;
            // Every bill, newest first: a filter left on (posted only, another day, a search)
            // would hide the bills the operator has asked to see.
            Bills.ClearFiltersCommand.Execute(null);
        };
        // "Open bill" on a row of Settings → Tally check: the bill is fixed from Bill details.
        Settings.TallyCheck.OpenBillRequested += (_, billId) =>
        {
            ActiveDialog = ShellDialogs.BillDetails;
            _ = BillDetails.LoadAsync(billId);
        };

        // What Bills needs of the shell that is not a dialog: the Invoice tab, the health
        // check, and the two commands its row menu binds. Dialogs it reaches by itself.
        Bills.EditBillHandler = LoadBillForEditInInvoiceTabAsync;
        Bills.RefreshTallyHealthHandler = RefreshBillsTallyHealthAsync;
        Bills.OpenDetailsCommand = OpenBillDetailsCommand;
        Bills.PrintSelectedCommand = OpenSelectedBillsPrintPreviewCommand;

        Handover = new HandoverCoordinator(_billsApiClient, this);
    }

    /// <summary>Takes the bill hand-overs this app receives; see docs/18_bill_handover.md.</summary>
    public HandoverCoordinator Handover { get; }

    AdminTokenStore IHandoverHost.AdminTokens => _adminTokenStore;

    Task IHandoverHost.RequestAdminUnlockAsync(CancellationToken cancellationToken) => _dialogs.RequestAdminUnlockAsync(cancellationToken);

    Task<IReadOnlyList<bool>> IHandoverHost.PrintSavedBillsAsync(IReadOnlyList<BillResponse> bills) => _printCoordinator.PrintSavedBillsAsync(bills);

    void IHandoverHost.OpenSavedBillsPreview(IReadOnlyList<BillResponse> bills) => _printCoordinator.OpenSavedBillsPreview(bills);

    private async Task LoadBillForEditInInvoiceTabAsync(Guid billId, CancellationToken cancellationToken)
    {
        ActiveTab = NavTab.Invoice;
        await Invoice.LoadBillForEditAsync(billId, cancellationToken);
    }

    private async void OnInvoiceSaveCompletedReadyForPrint(object? sender, InvoicePrintSnapshot saved)
    {
        try
        {
            await _printCoordinator.HandleInvoiceSaveCompletedAsync(saved);
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
    public ChangeNumberDialogViewModel ChangeNumberDialog => _dialogs.ChangeNumberDialog;
    public ReasonPromptDialogViewModel ReasonPromptDialog => _dialogs.ReasonPromptDialog;
    public ConfirmDialogViewModel ConfirmDialog => _dialogs.ConfirmDialog;

    public TitleBarViewModel TitleBar { get; }
    public HealthClusterViewModel Health { get; }
    public StatusBarViewModel StatusBar { get; }
    public FKeyStripViewModel FKeys { get; }

    /// <summary>True while any overlay, including the confirm question, covers the tabs.</summary>
    public bool IsAnyDialogOpen => ActiveDialog is not null || ConfirmDialog.IsOpen;

    private bool CanUseBareKeyShortcut() =>
        !IsAnyDialogOpen
        && System.Windows.Input.Keyboard.FocusedElement is not (System.Windows.Controls.Primitives.TextBoxBase or System.Windows.Controls.PasswordBox);

    private bool CanAddInvoiceRow => ActiveTab == NavTab.Invoice && Invoice.AddRowCommand.CanExecute(null);

    private bool CanRemoveInvoiceRow => ActiveTab == NavTab.Invoice && Invoice.RemoveFocusedRowCommand.CanExecute(null);

    // Printing an estimate needs at least one line on it.
    private bool CanPrintEstimate => Invoice.ItemCount > 0;

    private bool CanOpenBillDetails => Bills.SelectedBill is not null;

    private bool CanPrintSelectedBills => Bills.CanPrintSelected;

    private bool CanOpenFinalPrintPreview => BillDetails.Bill is not null;

    [ObservableProperty]
    private NavTab activeTab = NavTab.Invoice;

    [ObservableProperty]
    private SystemState systemState = SystemState.Degraded;

    /// <summary>
    /// The open dialog, one of the <see cref="ShellDialogs"/> names, or null. The dialog
    /// service holds it, so that a view model can open a prompt without the shell.
    /// </summary>
    public string? ActiveDialog
    {
        get => _dialogs.ActiveDialog;
        set => _dialogs.ActiveDialog = value;
    }

    [ObservableProperty]
    private string bannerText = string.Empty;

    [ObservableProperty]
    private SystemHealthSnapshot? lastHealthSnapshot;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastHealthCheckedDisplay))]
    private DateTimeOffset? lastHealthCheckedAtUtc;

    [ObservableProperty]
    private string? mastersRefreshMessage;

    [ObservableProperty]
    private bool isRefreshingAllMasters;

    public string LastHealthCheckedDisplay =>
        LastHealthCheckedAtUtc is null
            ? "never"
            : LastHealthCheckedAtUtc.Value.ToLocalTime().ToString("HH:mm:ss");

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
    [RelayCommand]
    private void DismissBanner()
    {
        _dismissedBannerText = BannerText;
        RaiseBannerChanged();
    }

    private string? _dismissedBannerText;

    partial void OnActiveTabChanged(NavTab value)
    {
        FKeys.SetScreen(value, SystemState == SystemState.Limited);
        AddInvoiceRowCommand.NotifyCanExecuteChanged();
        RemoveInvoiceRowCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsInvoiceVisible));
        OnPropertyChanged(nameof(IsBillsVisible));
        OnPropertyChanged(nameof(IsSettingsVisible));
        OnPropertyChanged(nameof(IsLimitedVisible));
        if (value == NavTab.Bills) _ = Bills.LoadAsync();
        if (value == NavTab.Invoice) _ = Invoice.RefreshNextNumberAsync();
        if (value == NavTab.Settings) _ = Settings.RefreshOnShownAsync();
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

    [RelayCommand]
    public void SwitchTab(NavTab tab) => ActiveTab = tab;

    [RelayCommand]
    private void OpenShortcuts() => ActiveDialog = ShellDialogs.Shortcuts;

    [RelayCommand]
    private void OpenAdminUnlock()
    {
        ActiveDialog = ShellDialogs.Admin;
        _ = Admin.LoadStatusCommand.ExecuteAsync(null);
    }

    // Bare-key variants for the window key bindings. A false CanExecute lets the key fall
    // through, so "?" and "`" still type into text boxes, and an open (possibly awaited)
    // prompt is never replaced.
    [RelayCommand(CanExecute = nameof(CanUseBareKeyShortcut))]
    private void OpenShortcutsKey() => OpenShortcuts();

    [RelayCommand(CanExecute = nameof(CanUseBareKeyShortcut))]
    private void OpenAdminUnlockKey() => OpenAdminUnlock();

    [RelayCommand]
    private void OpenSyntheticBatch()
    {
        SyntheticBatch.RefreshKaratOptions();
        if (SyntheticBatch.Rate24Kt <= 0m && Invoice.Rate24Kt is > 0m)
        {
            SyntheticBatch.Rate24Kt = Invoice.Rate24Kt.Value;
        }

        // The question before a batch warns when the rate typed there is far from this one.
        SyntheticBatch.InvoiceRate24Kt = Invoice.Rate24Kt;
        ActiveDialog = ShellDialogs.SyntheticBatch;
        // Start at the last bill and end now, as of this opening.
        _ = SyntheticBatch.PrepareWindowAsync();
    }

    // Ctrl+Alt+G, the hidden key V1 had: opens the batch data scheduler while admin is unlocked
    // and no dialog is open, and does nothing otherwise (the key is then left alone).
    [RelayCommand(CanExecute = nameof(CanUseBatchSchedulerKey))]
    private void OpenSyntheticBatchKey() => OpenSyntheticBatch();

    private bool CanUseBatchSchedulerKey() => Admin.IsUnlocked && !IsAnyDialogOpen;

    [RelayCommand]
    private void OpenHealth()
    {
        ActiveDialog = ShellDialogs.Health;
        _ = RefreshHealthAsync(forceTallyCompany: true);
    }

    // Not behind an open question or dialog: Save asks before it saves, and a row removed while the
    // question is open would be gone from the bill the operator then said yes to.
    [RelayCommand(CanExecute = nameof(CanAddInvoiceRow))]
    private void AddInvoiceRow()
    {
        if (!IsAnyDialogOpen) Invoice.AddRowCommand.Execute(null);
    }

    [RelayCommand(CanExecute = nameof(CanRemoveInvoiceRow))]
    private void RemoveInvoiceRow()
    {
        if (!IsAnyDialogOpen) Invoice.RemoveFocusedRowCommand.Execute(null);
    }

    /// <summary>Esc: answers the confirm question, cancels a prompt, closes the dialog, or discards a Settings edit.</summary>
    [RelayCommand]
    private void CloseDialog()
    {
        // The confirm overlay sits above everything, so Esc answers it first.
        if (ConfirmDialog.IsOpen)
        {
            ConfirmDialog.Cancel();
            return;
        }

        // A prompt someone awaits is answered "cancelled", so its caller goes on instead of
        // waiting for ever.
        if (_dialogs.CancelPrompt())
        {
            return;
        }

        // The key dialog forgets what was pasted into it when it closes.
        if (ActiveDialog == ShellDialogs.HandoverKey)
        {
            Settings.Handover.CancelKeyCommand.Execute(null);
            return;
        }

        if (ActiveDialog is not null)
        {
            ActiveDialog = null;
            return;
        }

        if (ActiveTab == NavTab.Settings && Settings.DiscardChangesCommand.CanExecute(null))
        {
            Settings.DiscardChangesCommand.Execute(null);
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenFinalPrintPreview))]
    private void OpenFinalPrintPreview() => _printCoordinator.OpenFinalPrintPreview();

    [RelayCommand]
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

    [RelayCommand(CanExecute = nameof(CanPrintEstimate))]
    private void PrintEstimate()
    {
        if (!IsAnyDialogOpen && ActiveTab == NavTab.Invoice)
        {
            _printCoordinator.OpenInvoicePreview();
        }
    }

    /// <summary>Ctrl+Shift+N: the Invoice tab's New bill. Ctrl+N is New row.</summary>
    [RelayCommand]
    private async Task NewInvoiceBillAsync()
    {
        if (IsAnyDialogOpen || !IsInvoiceVisible || !Invoice.NewBillCommand.CanExecute(null))
        {
            return;
        }

        await Invoice.NewBillCommand.ExecuteAsync(null);
    }

    [RelayCommand(CanExecute = nameof(CanOpenBillDetails))]
    private void OpenBillDetails()
    {
        var selected = Bills.SelectedBill;
        if (selected is null) return;
        ActiveDialog = ShellDialogs.BillDetails;
        _ = BillDetails.LoadAsync(selected.Id);
    }

    [RelayCommand(CanExecute = nameof(CanPrintSelectedBills))]
    private async Task OpenSelectedBillsPrintPreviewAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _printCoordinator.OpenSelectedBillsPrintPreviewAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            Bills.ReportProblem($"Print preview failed: {ex.Message}");
        }
    }

    public async Task InitializeAsync()
    {
        var settingsTask = ApplySettingsAsync();
        // The forced check also brings the database identity for the status bar.
        var healthTask = RefreshHealthAsync(forceTallyCompany: true);

        var settingsResponse = await settingsTask;
        _ = _printCoordinator.WarmUpPrintPipelineAsync();

        // A hand-over link that started the app is taken from here on. It needs what the
        // settings step has just applied (settings and masters, the company profile, the copy
        // defaults and the print layout), not the health check, which takes as long as Tally
        // takes. If the settings did not come, the hand-over asks for them once more itself.
        // The one case that waits is a PC whose first setup is still due: there the link is
        // taken once the setup form has opened, as it always was (MainWindow marks it then).
        if (!SetupWizard.IsDue(settingsResponse))
        {
            Handover.MarkStarted();
        }

        await healthTask;
        // Invoice is the default tab and OnActiveTabChanged does NOT fire for
        // the constructor's same-value assignment, so the preview fetch
        // wouldn't otherwise run on a cold launch where the operator stays put.
        // Triggering it explicitly here primes the numbering preview + warms
        // the connection pool while the user is still settling into the form.
        if (ActiveTab == NavTab.Invoice) _ = Invoice.RefreshNextNumberAsync();

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

    [RelayCommand]
    private void OpenServerSettings()
    {
        Settings.SelectedSection = SettingsSectionKey.Server;
        ActiveTab = NavTab.Settings;
    }

    private void StartHealthPolling()
    {
        if (_healthTimer is not null) return;
        _healthTimer = new DispatcherTimer { Interval = HealthPollInterval };
        _healthTimer.Tick += async (_, _) =>
        {
            try
            {
                await RefreshHealthAsync();
            }
            catch
            {
                // A poll that fails is repeated at the next tick. Nothing
                // awaits this handler, so an error let out of it would
                // reach the app as one nobody caught.
            }
        };
        _healthTimer.Start();
    }

    [RelayCommand]
    private async Task RefreshAllMastersAsync()
        => await _healthCoordinator.RefreshAllMastersAsync();

    /// <summary>The Check again / Retry buttons: a poll that also asks Tally which company is open.</summary>
    [RelayCommand]
    private Task RefreshHealth(CancellationToken cancellationToken)
        => RefreshHealthAsync(forceTallyCompany: true, cancellationToken);

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
