using ShowroomBilling.Contracts.Masters;
using ShowroomBilling.Contracts.Runtime;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.Services.ProcessSupervision;
using ShowroomBilling.Desktop.ViewModels.Setup;

namespace ShowroomBilling.Desktop.Tests;

public sealed class SetupWizardViewModelTests
{
    [Fact]
    public async Task PrepareForStartupAsync_OpensDatabaseStep_WhenBootstrapIsAvailable()
    {
        var vm = BuildVm(
            runtime: new FakeRuntimeApiClient
            {
                Database = Database(canBootstrap: true, localOverride: false)
            });

        var shouldOpen = await vm.PrepareForStartupAsync(Healthy(), null);

        Assert.True(shouldOpen);
        Assert.Equal(SetupWizardStep.Database, vm.CurrentStep);
    }

    [Fact]
    public async Task PrepareForStartupAsync_OpensBasicsStep_WhenSettingsRequireSetup()
    {
        var vm = BuildVm(
            runtime: new FakeRuntimeApiClient
            {
                Database = Database(canBootstrap: false, localOverride: true)
            },
            settings: new FakeSettingsApiClient(Settings(requiresInitialSetup: true)));

        var shouldOpen = await vm.PrepareForStartupAsync(Healthy(), null);

        Assert.True(shouldOpen);
        Assert.Equal(SetupWizardStep.Basics, vm.CurrentStep);
    }

    [Fact]
    public async Task PrepareForStartupAsync_DoesNotOpen_WhenConfiguredAndSettingsAreReal()
    {
        var vm = BuildVm(
            runtime: new FakeRuntimeApiClient
            {
                Database = Database(canBootstrap: false, localOverride: true)
            },
            settings: new FakeSettingsApiClient(Settings(requiresInitialSetup: false)));

        var shouldOpen = await vm.PrepareForStartupAsync(Healthy(), null);

        Assert.False(shouldOpen);
    }

    [Fact]
    public async Task SaveDatabaseCommand_RestartsApi_AndAdvancesToBasics()
    {
        var runtime = new FakeRuntimeApiClient
        {
            Database = Database(canBootstrap: true, localOverride: false),
            BootstrapResponse = Database(canBootstrap: false, localOverride: true, requiresRestart: true)
        };
        var supervisor = new FakeChildProcessSupervisor { CanRestartApi = true };
        var vm = BuildVm(
            runtime: runtime,
            settings: new FakeSettingsApiClient(Settings(requiresInitialSetup: true)),
            supervisor: supervisor);

        await vm.PrepareForStartupAsync(Healthy(), null);
        vm.DatabaseConnectionString = "Host=db;Database=showroom;Username=user;Password=secret";

        await vm.SaveDatabaseCommand.ExecuteAsync(null);

        Assert.Equal(1, supervisor.RestartCount);
        Assert.Equal(SetupWizardStep.Basics, vm.CurrentStep);
    }

    [Fact]
    public async Task SaveDatabaseCommand_ShowsRestartAction_WhenApiIsNotManaged()
    {
        var runtime = new FakeRuntimeApiClient
        {
            Database = Database(canBootstrap: true, localOverride: false),
            BootstrapResponse = Database(canBootstrap: false, localOverride: true, requiresRestart: true)
        };
        var vm = BuildVm(
            runtime: runtime,
            supervisor: new FakeChildProcessSupervisor { CanRestartApi = false });

        await vm.PrepareForStartupAsync(Healthy(), null);
        vm.DatabaseConnectionString = "Host=db;Database=showroom;Username=user;Password=secret";

        await vm.SaveDatabaseCommand.ExecuteAsync(null);

        Assert.True(vm.RequiresDesktopRestart);
        Assert.True(vm.HasStatusMessage);
        Assert.False(vm.HasErrorMessage);
        Assert.False(vm.IsDatabaseSaveVisible);
        Assert.Contains("Restart the desktop app", vm.StatusMessage);
    }

    [Fact]
    public async Task SaveBasicsCommand_SavesSettings_AndMarksComplete()
    {
        var settings = new FakeSettingsApiClient(Settings(requiresInitialSetup: true));
        var marker = new FakeSetupWizardCompletionStore();
        var vm = BuildVm(
            runtime: new FakeRuntimeApiClient
            {
                Database = Database(canBootstrap: false, localOverride: true)
            },
            settings: settings,
            marker: marker);

        await vm.PrepareForStartupAsync(Healthy(), null);
        vm.ActiveCompanyName = "Showroom Alpha";
        vm.PrintCompanyName = "Alpha Jewellers";
        vm.InvoicePrefix = "SB-";
        vm.SalesLedger = "Alpha Sales";
        vm.CashLedger = "Alpha Cash";
        vm.CreditDebitLedger = "Alpha Card";
        vm.CgstLedger = "Alpha CGST";
        vm.SgstLedger = "Alpha SGST";
        vm.RoundOffLedger = "Alpha Round Off";
        vm.DiscountLedger = "Alpha Discount";
        vm.SalesVoucherType = "Alpha Sales Voucher";

        await vm.SaveBasicsCommand.ExecuteAsync(null);

        Assert.True(marker.Completed);
        Assert.NotNull(settings.LastSaved);
        Assert.Equal("Showroom Alpha", settings.LastSaved!.Connection.ActiveCompanyName);
    }

    private static SetupWizardViewModel BuildVm(
        FakeRuntimeApiClient? runtime = null,
        FakeSettingsApiClient? settings = null,
        FakeChildProcessSupervisor? supervisor = null,
        FakeSetupWizardCompletionStore? marker = null)
    {
        return new SetupWizardViewModel(
            runtime ?? new FakeRuntimeApiClient(),
            new FakeHealthApiClient(Healthy()),
            settings ?? new FakeSettingsApiClient(Settings(requiresInitialSetup: false)),
            supervisor ?? new FakeChildProcessSupervisor(),
            marker ?? new FakeSetupWizardCompletionStore());
    }

    private static DatabaseConfigurationResponse Database(
        bool canBootstrap,
        bool localOverride,
        bool requiresRestart = false) =>
        new(
            "PostgreSQL",
            string.Empty,
            "Host=db;Database=showroom;Username=user;Password=***",
            "database.Production.local.json",
            localOverride,
            requiresRestart,
            "Production",
            false,
            "Windows DPAPI CurrentUser",
            canBootstrap);

    private static SystemHealthSnapshot Healthy() =>
        new(
            true,
            null,
            null,
            new RuntimeHealthResponse(
                "Healthy",
                true,
                true,
                true,
                true,
                "Database ready.",
                "PROD",
                "PROD",
                true));

    private static EffectiveSettingsResponse Settings(bool requiresInitialSetup) =>
        new(
            "cloud",
            "settings",
            new EffectiveCloudSettingsDto(
                new ConnectionSettingsDto("127.0.0.1", 9000, 30, "Development Company"),
                new NumberingSettingsDto("DEV-", string.Empty, 4),
                new PrintSettingsDto("Tally Wrapper", null, null, null, null, "India", null, null, null, null, null, true, false, false, 11, 9),
                new LedgerMappingsDto("Sales", "Cash", "Card / UPI", "CGST", "SGST", "Round Off", "Discount", "Sales"),
                new MasterDataSettingsDto("[]", "[]")),
            [],
            [],
            DateTimeOffset.UtcNow,
            requiresInitialSetup);

    private sealed class FakeRuntimeApiClient : IRuntimeApiClient
    {
        public DatabaseConfigurationResponse Database { get; set; } =
            Database(canBootstrap: false, localOverride: true);

        public DatabaseConfigurationResponse? BootstrapResponse { get; set; }

        public Task<RuntimeBootstrapResponse> GetBootstrapAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DatabaseConfigurationResponse> GetDatabaseConfigurationAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Database);

        public Task<DatabaseConfigurationTestResponse> TestDatabaseConfigurationAsync(
            TestDatabaseConfigurationRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DatabaseConfigurationTestResponse(true, "Connection succeeded."));

        public Task<DatabaseConfigurationResponse> UpdateDatabaseConfigurationAsync(
            UpdateDatabaseConfigurationRequest request,
            string adminToken,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DatabaseConfigurationResponse> BootstrapDatabaseConfigurationAsync(
            UpdateDatabaseConfigurationRequest request,
            CancellationToken cancellationToken = default)
        {
            Database = BootstrapResponse ?? Database(canBootstrap: false, localOverride: true, requiresRestart: false);
            return Task.FromResult(Database);
        }
    }

    private sealed class FakeHealthApiClient(SystemHealthSnapshot snapshot) : IHealthApiClient
    {
        public Task<SystemHealthSnapshot> GetSnapshotAsync(
            bool includeTallyCompany,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(snapshot);
    }

    private sealed class FakeSettingsApiClient(EffectiveSettingsResponse response) : ISettingsApiClient
    {
        public EffectiveCloudSettingsDto? LastSaved { get; private set; }

        public Task<EffectiveSettingsResponse> GetEffectiveSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(response);

        public Task<SettingsUpdateResponse> SaveEffectiveSettingsAsync(
            UpdateEffectiveSettingsRequest request,
            CancellationToken cancellationToken = default)
        {
            LastSaved = request.Settings;
            return Task.FromResult(new SettingsUpdateResponse("cloud", "saved", ["connection"], DateTimeOffset.UtcNow));
        }

        public Task<SettingsUpdateResponse> SelectActiveCompanyAsync(
            SelectActiveCompanyRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PrintLayoutResponse> GetPrintLayoutAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PrintLayoutResponse> UpdatePrintLayoutAsync(
            UpdatePrintLayoutRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeChildProcessSupervisor : IChildProcessSupervisor
    {
        public bool CanRestartApi { get; set; }

        public int RestartCount { get; private set; }

        public bool RestartApi()
        {
            RestartCount++;
            return true;
        }
    }

    private sealed class FakeSetupWizardCompletionStore : ISetupWizardCompletionStore
    {
        public bool Completed { get; private set; }

        public bool IsComplete() => Completed;

        public Task MarkCompleteAsync(CancellationToken cancellationToken = default)
        {
            Completed = true;
            return Task.CompletedTask;
        }
    }
}
