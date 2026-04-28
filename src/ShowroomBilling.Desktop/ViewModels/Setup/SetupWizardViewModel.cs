using System.Globalization;
using System.Net.Http;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShowroomBilling.Contracts.Runtime;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.Services.ProcessSupervision;

namespace ShowroomBilling.Desktop.ViewModels.Setup;

public partial class SetupWizardViewModel : ObservableObject
{
    private static readonly TimeSpan DatabaseReadyTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DatabaseReadyPollInterval = TimeSpan.FromMilliseconds(500);

    private readonly IRuntimeApiClient _runtimeApi;
    private readonly IHealthApiClient _healthApi;
    private readonly ISettingsApiClient _settingsApi;
    private readonly IChildProcessSupervisor? _childProcessSupervisor;
    private readonly ISetupWizardCompletionStore _completionStore;

    private EffectiveCloudSettingsDto? _baselineSettings;

    public SetupWizardViewModel(
        IRuntimeApiClient runtimeApi,
        IHealthApiClient healthApi,
        ISettingsApiClient settingsApi,
        IChildProcessSupervisor? childProcessSupervisor,
        ISetupWizardCompletionStore completionStore)
    {
        _runtimeApi = runtimeApi;
        _healthApi = healthApi;
        _settingsApi = settingsApi;
        _childProcessSupervisor = childProcessSupervisor;
        _completionStore = completionStore;

        SaveDatabaseCommand = new AsyncRelayCommand(SaveDatabaseAsync, CanSaveDatabase);
        SaveBasicsCommand = new AsyncRelayCommand(SaveBasicsAsync, CanSaveBasics);
        RestartDesktopCommand = new RelayCommand(RestartDesktop, () => RequiresDesktopRestart);
    }

    public event EventHandler? Completed;

    public IAsyncRelayCommand SaveDatabaseCommand { get; }

    public IAsyncRelayCommand SaveBasicsCommand { get; }

    public IRelayCommand RestartDesktopCommand { get; }

    public Action RestartDesktopAction { get; set; } = RestartCurrentProcess;

    [ObservableProperty] private SetupWizardStep currentStep = SetupWizardStep.Database;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool requiresDesktopRestart;
    [ObservableProperty] private string statusMessage = string.Empty;
    [ObservableProperty] private string errorMessage = string.Empty;
    [ObservableProperty] private string databaseConnectionString = string.Empty;
    [ObservableProperty] private string tallyHost = "127.0.0.1";
    [ObservableProperty] private string tallyPort = "9000";
    [ObservableProperty] private string tallyTimeoutSeconds = "30";
    [ObservableProperty] private string activeCompanyName = string.Empty;
    [ObservableProperty] private string printCompanyName = string.Empty;
    [ObservableProperty] private string invoicePrefix = string.Empty;
    [ObservableProperty] private string invoiceSuffix = string.Empty;
    [ObservableProperty] private string invoicePadding = "4";
    [ObservableProperty] private string salesLedger = string.Empty;
    [ObservableProperty] private string cashLedger = string.Empty;
    [ObservableProperty] private string creditDebitLedger = string.Empty;
    [ObservableProperty] private string cgstLedger = string.Empty;
    [ObservableProperty] private string sgstLedger = string.Empty;
    [ObservableProperty] private string roundOffLedger = string.Empty;
    [ObservableProperty] private string discountLedger = string.Empty;
    [ObservableProperty] private string salesVoucherType = string.Empty;

    public bool IsDatabaseStep => CurrentStep == SetupWizardStep.Database;
    public bool IsDatabaseSaveVisible => IsDatabaseStep && !RequiresDesktopRestart;
    public bool IsBasicsStep => CurrentStep == SetupWizardStep.Basics;
    public bool IsCompleteStep => CurrentStep == SetupWizardStep.Complete;
    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);
    public bool HasErrorMessage => !string.IsNullOrWhiteSpace(ErrorMessage);
    public string StepLabel => CurrentStep switch
    {
        SetupWizardStep.Database => "STEP 1 OF 2",
        SetupWizardStep.Basics => "STEP 2 OF 2",
        _ => "SETUP COMPLETE"
    };

    public async Task<bool> PrepareForStartupAsync(
        SystemHealthSnapshot? healthSnapshot,
        EffectiveSettingsResponse? settingsResponse,
        CancellationToken cancellationToken = default)
    {
        ClearMessages();

        DatabaseConfigurationResponse? database = null;
        try
        {
            database = await _runtimeApi.GetDatabaseConfigurationAsync(cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            SetError($"Setup check failed: {ApiResponseReader.FormatError(ex)}");
            return false;
        }

        var databaseStepNeeded = NeedsDatabaseStep(database, healthSnapshot);
        var settings = settingsResponse;
        if (!databaseStepNeeded && settings is null)
        {
            try
            {
                settings = await _settingsApi.GetEffectiveSettingsAsync(cancellationToken);
            }
            catch
            {
                settings = null;
            }
        }

        var basicsStepNeeded = settings?.RequiresInitialSetup == true && !_completionStore.IsComplete();
        if (!databaseStepNeeded && !basicsStepNeeded)
        {
            return false;
        }

        if (databaseStepNeeded)
        {
            CurrentStep = SetupWizardStep.Database;
            SetStatus("Paste the PostgreSQL connection string to finish first-run database setup.");
            return true;
        }

        LoadBasics(settings!.Settings);
        CurrentStep = SetupWizardStep.Basics;
        SetStatus("Review the basic showroom and Tally settings before billing starts.");
        return true;
    }

    private static bool NeedsDatabaseStep(
        DatabaseConfigurationResponse database,
        SystemHealthSnapshot? healthSnapshot)
    {
        if (database.CanBootstrapWithoutAdmin)
        {
            return true;
        }

        var runtime = healthSnapshot?.Runtime;
        return runtime is { DatabaseReachable: false }
            && !database.IsLocalOverridePresent
            && !database.IsEnvironmentOverridePresent;
    }

    private bool CanSaveDatabase() =>
        !IsBusy && !RequiresDesktopRestart && !string.IsNullOrWhiteSpace(DatabaseConnectionString);

    private bool CanSaveBasics() => !IsBusy && !RequiresDesktopRestart;

    private async Task SaveDatabaseAsync(CancellationToken cancellationToken)
    {
        ClearMessages();
        IsBusy = true;
        try
        {
            SetStatus("Testing database connection...");
            var test = await _runtimeApi.TestDatabaseConfigurationAsync(
                new TestDatabaseConfigurationRequest(DatabaseConnectionString),
                cancellationToken);
            if (!test.Success)
            {
                SetError(test.Message);
                return;
            }

            SetStatus("Saving encrypted database configuration...");
            var response = await _runtimeApi.BootstrapDatabaseConfigurationAsync(
                new UpdateDatabaseConfigurationRequest(DatabaseConnectionString),
                cancellationToken);

            if (response.RequiresApiRestart)
            {
                if (_childProcessSupervisor?.CanRestartApi != true)
                {
                    RequiresDesktopRestart = true;
                    SetStatus("Database was saved. Restart the desktop app to apply it, then reopen setup.");
                    return;
                }

                SetStatus("Restarting embedded API...");
                var restarted = await Task.Run(() => _childProcessSupervisor.RestartApi(), cancellationToken);
                if (!restarted)
                {
                    RequiresDesktopRestart = true;
                    SetStatus("Database was saved, but the embedded API did not restart. Restart the desktop app.");
                    return;
                }

                var ready = await WaitForDatabaseReadyAsync(cancellationToken);
                if (!ready)
                {
                    SetError("The API restarted, but the database is not ready yet. Check the connection string.");
                    return;
                }
            }

            SetStatus("Loading shared settings...");
            var settings = await _settingsApi.GetEffectiveSettingsAsync(cancellationToken);
            if (settings.RequiresInitialSetup && !_completionStore.IsComplete())
            {
                LoadBasics(settings.Settings);
                CurrentStep = SetupWizardStep.Basics;
                SetStatus("Database is ready. Finish the basic showroom settings.");
                return;
            }

            await CompleteAsync(cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            SetError(ApiResponseReader.FormatError(ex));
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SaveBasicsAsync(CancellationToken cancellationToken)
    {
        ClearMessages();
        if (_baselineSettings is null)
        {
            SetError("Shared settings are not loaded yet.");
            return;
        }

        if (!TryBuildBasicsDto(_baselineSettings, out var dto, out var error))
        {
            SetError(error);
            return;
        }

        IsBusy = true;
        SetStatus("Saving basic settings...");
        try
        {
            await _settingsApi.SaveEffectiveSettingsAsync(new UpdateEffectiveSettingsRequest(dto), cancellationToken);
            await CompleteAsync(cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            SetError(ApiResponseReader.FormatError(ex));
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CompleteAsync(CancellationToken cancellationToken)
    {
        await _completionStore.MarkCompleteAsync(cancellationToken);
        CurrentStep = SetupWizardStep.Complete;
        SetStatus("Setup complete.");
        Completed?.Invoke(this, EventArgs.Empty);
    }

    private async Task<bool> WaitForDatabaseReadyAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + DatabaseReadyTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var snapshot = await _healthApi.GetSnapshotAsync(includeTallyCompany: false, cancellationToken);
            if (snapshot.ApiReachable
                && snapshot.Runtime is { DatabaseConfigured: true, DatabaseReachable: true }
                && snapshot.Runtime.DatabaseIdentityMatches != false)
            {
                return true;
            }

            await Task.Delay(DatabaseReadyPollInterval, cancellationToken);
        }

        return false;
    }

    private void LoadBasics(EffectiveCloudSettingsDto settings)
    {
        _baselineSettings = settings;
        TallyHost = settings.Connection.Host;
        TallyPort = settings.Connection.Port.ToString(CultureInfo.InvariantCulture);
        TallyTimeoutSeconds = settings.Connection.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        ActiveCompanyName = settings.Connection.ActiveCompanyName;
        PrintCompanyName = settings.Print.CompanyName;
        InvoicePrefix = settings.Numbering.InvoicePrefix;
        InvoiceSuffix = settings.Numbering.InvoiceSuffix;
        InvoicePadding = settings.Numbering.InvoicePadding.ToString(CultureInfo.InvariantCulture);
        SalesLedger = settings.Ledgers.SalesLedger;
        CashLedger = settings.Ledgers.CashLedger;
        CreditDebitLedger = settings.Ledgers.CreditDebitLedger;
        CgstLedger = settings.Ledgers.CgstLedger;
        SgstLedger = settings.Ledgers.SgstLedger;
        RoundOffLedger = settings.Ledgers.RoundOffLedger;
        DiscountLedger = settings.Ledgers.DiscountLedger;
        SalesVoucherType = settings.Ledgers.SalesVoucherType;
    }

    private bool TryBuildBasicsDto(
        EffectiveCloudSettingsDto baseline,
        out EffectiveCloudSettingsDto dto,
        out string error)
    {
        dto = baseline;
        if (string.IsNullOrWhiteSpace(TallyHost)) { error = "Tally host is required."; return false; }
        if (!int.TryParse(TallyPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        { error = "Tally port must be between 1 and 65535."; return false; }
        if (!int.TryParse(TallyTimeoutSeconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timeout) || timeout < 1)
        { error = "Tally timeout must be a positive integer."; return false; }
        if (string.IsNullOrWhiteSpace(ActiveCompanyName)) { error = "Active company name is required."; return false; }
        if (string.IsNullOrWhiteSpace(PrintCompanyName)) { error = "Print company name is required."; return false; }
        if (!int.TryParse(InvoicePadding, NumberStyles.Integer, CultureInfo.InvariantCulture, out var padding) || padding is < 1 or > 10)
        { error = "Invoice padding must be between 1 and 10."; return false; }
        if (string.IsNullOrWhiteSpace(SalesLedger)
            || string.IsNullOrWhiteSpace(CashLedger)
            || string.IsNullOrWhiteSpace(CreditDebitLedger)
            || string.IsNullOrWhiteSpace(CgstLedger)
            || string.IsNullOrWhiteSpace(SgstLedger)
            || string.IsNullOrWhiteSpace(RoundOffLedger)
            || string.IsNullOrWhiteSpace(DiscountLedger)
            || string.IsNullOrWhiteSpace(SalesVoucherType))
        { error = "All ledger mappings and the sales voucher type are required."; return false; }

        dto = baseline with
        {
            Connection = new ConnectionSettingsDto(
                TallyHost.Trim(),
                port,
                timeout,
                ActiveCompanyName.Trim()),
            Numbering = new NumberingSettingsDto(
                InvoicePrefix.Trim(),
                InvoiceSuffix.Trim(),
                padding),
            Print = baseline.Print with { CompanyName = PrintCompanyName.Trim() },
            Ledgers = new LedgerMappingsDto(
                SalesLedger.Trim(),
                CashLedger.Trim(),
                CreditDebitLedger.Trim(),
                CgstLedger.Trim(),
                SgstLedger.Trim(),
                RoundOffLedger.Trim(),
                DiscountLedger.Trim(),
                SalesVoucherType.Trim())
        };
        error = string.Empty;
        return true;
    }

    private void ClearMessages()
    {
        ErrorMessage = string.Empty;
        StatusMessage = string.Empty;
        RequiresDesktopRestart = false;
    }

    private void SetStatus(string message)
    {
        ErrorMessage = string.Empty;
        StatusMessage = message;
    }

    private void SetError(string message)
    {
        StatusMessage = string.Empty;
        ErrorMessage = message;
    }

    private void RestartDesktop()
    {
        RestartDesktopAction();
    }

    private static void RestartCurrentProcess()
    {
        var executable = Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(executable))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = true
        });
        System.Windows.Application.Current.Shutdown();
    }

    partial void OnCurrentStepChanged(SetupWizardStep value)
    {
        OnPropertyChanged(nameof(IsDatabaseStep));
        OnPropertyChanged(nameof(IsDatabaseSaveVisible));
        OnPropertyChanged(nameof(IsBasicsStep));
        OnPropertyChanged(nameof(IsCompleteStep));
        OnPropertyChanged(nameof(StepLabel));
    }

    partial void OnIsBusyChanged(bool value)
    {
        SaveDatabaseCommand.NotifyCanExecuteChanged();
        SaveBasicsCommand.NotifyCanExecuteChanged();
    }

    partial void OnRequiresDesktopRestartChanged(bool value)
    {
        RestartDesktopCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsDatabaseSaveVisible));
        SaveDatabaseCommand.NotifyCanExecuteChanged();
        SaveBasicsCommand.NotifyCanExecuteChanged();
    }

    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatusMessage));
    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasErrorMessage));
    partial void OnDatabaseConnectionStringChanged(string value) => SaveDatabaseCommand.NotifyCanExecuteChanged();
}
