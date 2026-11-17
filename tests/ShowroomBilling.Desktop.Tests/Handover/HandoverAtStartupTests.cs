using System.IO;
using Microsoft.Extensions.Options;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Desktop.Configuration;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.Shell;
using ShowroomBilling.Desktop.Tests.TestSupport;
using ShowroomBilling.Desktop.ViewModels;
using ShowroomBilling.Desktop.ViewModels.Admin;
using ShowroomBilling.Desktop.ViewModels.Bills;
using ShowroomBilling.Desktop.ViewModels.Invoice;
using ShowroomBilling.Desktop.ViewModels.Printing;
using ShowroomBilling.Desktop.ViewModels.Setup;
using ShowroomBilling.Desktop.ViewModels.SyntheticBatch;
using ShowroomBilling.Printing;

namespace ShowroomBilling.Desktop.Tests.Handover;

/// <summary>
/// When the app, started by a hand-over link, takes that link: on the real shell, with a server
/// whose health check (the part that waits for Tally) is held back by the test. The link here
/// is one the app refuses, so "taken" shows as the hand-over's result dialog.
/// </summary>
public sealed class HandoverAtStartupTests
{
    private const string Link = "tallywrapper:handover?d=x&s=y";

    [Fact]
    public async Task The_link_is_taken_once_the_settings_are_in_while_the_health_check_is_still_out()
    {
        var server = new Server(HandoverTestMasters.Response());
        var shell = CreateShell(server, setupDoneOnThisPc: true);

        // As the app does: the link is queued before the window shows, then the shell loads.
        shell.Handover.Receive(Link);
        var starting = shell.InitializeAsync();

        await WaitForAsync(() => shell.ActiveDialog == ShellDialogs.HandoverResult);
        Assert.False(starting.IsCompleted);
        Assert.Equal(1, server.HealthChecksOut);

        // What the hand-over uses was there when it was let in.
        Assert.NotNull(shell.Settings.Settings);
        Assert.Equal("Sample Company", shell.TitleBar.Company);

        // The health check coming back changes nothing about it.
        server.AnswerHealth();
        await starting;
        Assert.Equal(ShellDialogs.HandoverResult, shell.ActiveDialog);
    }

    [Fact]
    public async Task With_the_server_not_answering_the_link_is_still_taken_before_the_health_check_ends()
    {
        var server = new Server(settings: null);
        var shell = CreateShell(server, setupDoneOnThisPc: true);

        shell.Handover.Receive(Link);
        var starting = shell.InitializeAsync();

        await WaitForAsync(() => shell.ActiveDialog == ShellDialogs.HandoverResult);
        Assert.False(starting.IsCompleted);

        server.AnswerHealth();
        await starting;
    }

    [Fact]
    public async Task On_a_PC_whose_first_setup_is_due_the_link_waits_until_the_setup_form_has_opened()
    {
        var server = new Server(HandoverTestMasters.Response(requiresInitialSetup: true));
        var shell = CreateShell(server, setupDoneOnThisPc: false);

        shell.Handover.Receive(Link);
        var starting = shell.InitializeAsync();

        // Settings are in, the health check is out: neither the form nor the hand-over yet.
        await WaitForAsync(() => shell.Settings.Settings is not null && server.HealthChecksOut == 1);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Null(shell.ActiveDialog);

        server.AnswerHealth();
        await starting;
        Assert.Equal(ShellDialogs.SetupWizard, shell.ActiveDialog);

        // The window lets the link in when start-up is over (MainWindow.OnLoaded), as before.
        shell.Handover.MarkStarted();
        await WaitForAsync(() => shell.ActiveDialog == ShellDialogs.HandoverResult);
    }

    [Fact]
    public async Task A_plain_start_ends_as_before_with_a_link_that_arrives_later_taken_at_once()
    {
        var server = new Server(HandoverTestMasters.Response());
        var shell = CreateShell(server, setupDoneOnThisPc: true);

        var starting = shell.InitializeAsync();
        server.AnswerHealth();
        await starting;
        Assert.Null(shell.ActiveDialog);

        // A link from a second start, handed over the pipe to this running copy.
        shell.Handover.Receive(Link);
        await WaitForAsync(() => shell.ActiveDialog == ShellDialogs.HandoverResult);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 500 && !condition(); attempt++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "The app did not get there within five seconds.");
    }

    private static MainWindowViewModel CreateShell(Server server, bool setupDoneOnThisPc)
    {
        var tokens = new AdminTokenStore();
        var admin = new AdminUnlockViewModel(adminApi: null!, tokens);
        var dialogs = new DialogService(tokens, admin);
        var settings = OfflineApi.Settings(
            server,
            handoverKeys: new HandoverKeyStore(Path.Combine(TestAppData.Root, "keys", Guid.NewGuid().ToString("N"), HandoverKeyStore.FileName)),
            dialogs: dialogs);
        var billsApi = new FakeBillsApi();

        return new MainWindowViewModel(
            settingsApiClient: server,
            healthApiClient: server,
            mastersApiClient: new OfflineMastersApi(),
            billsApiClient: billsApi,
            bootstrapOptions: Options.Create(new DesktopBootstrapOptions()),
            companyProfileProvider: new CompanyProfileProvider(),
            printLayoutOptionsProvider: new PrintLayoutOptionsProvider(server, new OfflinePrintAssetApi()),
            printDispatcher: new NoPrinters(),
            printPreferences: new NoPreferences(),
            adminTokenStore: tokens,
            dialogs: dialogs,
            invoice: new InvoiceViewModel(billsApi, FakeNumberingApi.Offline(), dialogs, settings),
            bills: new BillsViewModel(billsApi, dialogs, tokens, settings),
            billDetails: new BillDetailsViewModel(billsApi, dialogs),
            printPreview: new PrintPreviewViewModel(dispatcher: null, preferences: null),
            settings: settings,
            admin: admin,
            setupWizard: new SetupWizardViewModel(server, new SetupMarker(setupDoneOnThisPc)),
            syntheticBatch: new SyntheticBatchViewModel(billsApi, dialogs, adminTokens: null, settings: null));
    }

    /// <summary>
    /// The server as the shell's start-up meets it: settings at once (or not at all), and a
    /// health check that stays out until the test answers it, as one that waits for Tally does.
    /// </summary>
    private sealed class Server(EffectiveSettingsResponse? settings) : ISettingsApiClient, IHealthApiClient
    {
        private readonly TaskCompletionSource _health = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly OfflineSettingsApi _offline = new();
        private int _healthChecksOut;

        public int HealthChecksOut => Volatile.Read(ref _healthChecksOut);

        public void AnswerHealth() => _health.TrySetResult();

        public Task<EffectiveSettingsResponse> GetEffectiveSettingsAsync(CancellationToken cancellationToken = default)
            => settings is null ? _offline.GetEffectiveSettingsAsync(cancellationToken) : Task.FromResult(settings);

        public async Task<SystemHealthSnapshot> GetSnapshotAsync(
            bool includeTallyCompany,
            bool includeMasterFreshness = true,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _healthChecksOut);
            await _health.Task;
            return settings is null ? SystemHealthSnapshot.Unreachable() : new SystemHealthSnapshot(true, null, null);
        }

        public Task<SettingsUpdateResponse> SaveEffectiveSettingsAsync(UpdateEffectiveSettingsRequest request, CancellationToken cancellationToken = default)
            => _offline.SaveEffectiveSettingsAsync(request, cancellationToken);

        public Task<SettingsUpdateResponse> SelectActiveCompanyAsync(SelectActiveCompanyRequest request, CancellationToken cancellationToken = default)
            => _offline.SelectActiveCompanyAsync(request, cancellationToken);

        // The layout does not come; the shell prints with the one it has, as on a server without one.
        public Task<PrintLayoutResponse> GetPrintLayoutAsync(CancellationToken cancellationToken = default)
            => _offline.GetPrintLayoutAsync(cancellationToken);

        public Task<PrintLayoutResponse> UpdatePrintLayoutAsync(UpdatePrintLayoutRequest request, CancellationToken cancellationToken = default)
            => _offline.UpdatePrintLayoutAsync(request, cancellationToken);
    }

    private sealed class SetupMarker(bool complete) : ISetupWizardCompletionStore
    {
        public bool IsComplete() => complete;

        public Task MarkCompleteAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoPrinters : IPrintDispatcher
    {
        public IReadOnlyList<string> AvailablePrinters() => [];
        public string? DefaultPrinter() => null;
        public void RefreshPrinters() { }
        public PrintJobCapabilities GetPrinterCapabilities(string printerName) => PrintJobCapabilities.Unknown;
        public byte[] GeneratePdf(PrintDocumentOptions options) => [];
        public IReadOnlyList<byte[]> GeneratePageImages(PrintDocumentOptions options, int dpi = 150) => [];
        public IReadOnlyList<byte[]> GeneratePrintPageImages(IReadOnlyList<PrintDocumentOptions> options) => [];
        public bool PrintToPrinter(PrintDocumentOptions options, string printerName, PrintJobSettings? settings = null) => false;
        public bool PrintToPrinter(IReadOnlyList<PrintDocumentOptions> options, string printerName, PrintJobSettings? settings = null) => false;
        public bool PrintRenderedPages(IReadOnlyList<byte[]> pageImages, string printerName, PrintJobSettings? settings = null) => false;
    }

    private sealed class NoPreferences : IPrintPreferencesStore
    {
        public event EventHandler<double>? PrintPreviewZoomPercentChanged
        {
            add { }
            remove { }
        }

        public string? LastPrinterName => null;
        public string? LastPdfDirectory => null;
        public bool DirectPrintAfterSave => false;
        public double PrintPreviewZoomPercent => 100;
        public PrintJobSettings PrintJobSettings => PrintJobSettings.Default;

        public void SaveLastPrinter(string printerName) { }
        public void SaveLastPdfDirectory(string directory) { }
        public void SaveDirectPrintAfterSave(bool value) { }
        public void SavePrintPreviewZoomPercent(double value) { }
        public void SavePrintJobSettings(PrintJobSettings settings) { }
    }
}
