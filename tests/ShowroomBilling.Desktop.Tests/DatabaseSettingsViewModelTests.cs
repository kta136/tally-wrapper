using System.IO;
using ShowroomBilling.Contracts.Admin;
using ShowroomBilling.Contracts.Leases;
using ShowroomBilling.Contracts.Masters;
using ShowroomBilling.Contracts.Runtime;
using ShowroomBilling.Desktop.Configuration;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.Services.ProcessSupervision;
using ShowroomBilling.Desktop.ViewModels.Admin;
using ShowroomBilling.Desktop.ViewModels.Settings;

namespace ShowroomBilling.Desktop.Tests;

public sealed class DatabaseSettingsViewModelTests
{
    [Fact]
    public async Task SaveApiConnectionMode_WritesBootstrapOverride_AndRestarts()
    {
        using var bootstrapFile = new BootstrapOverrideFileScope();
        var restartCount = 0;
        var vm = new DatabaseSettingsViewModel(
            bootstrapOptions: new DesktopBootstrapOptions
            {
                ConnectionMode = DesktopConnectionModes.LocalEmbedded,
                ApiBaseUrl = "http://localhost:5107"
            },
            restartApplication: () => restartCount++);

        vm.ApiConnectionMode = DesktopConnectionModes.Server;
        vm.ServerApiBaseUrl = "http://192.168.0.10:5107/api/health";

        await vm.SaveApiConnectionModeCommand.ExecuteAsync(null);

        var pairs = DesktopBootstrapLocalOverrideStore.LoadConfigurationPairs();
        Assert.Equal(DesktopConnectionModes.Server, pairs["DesktopBootstrap:ConnectionMode"]);
        Assert.Equal("http://192.168.0.10:5107", pairs["DesktopBootstrap:ServerApiBaseUrl"]);
        Assert.Equal(1, restartCount);
        Assert.Contains("Saved Server", vm.ApiConnectionStatus);
    }

    [Fact]
    public async Task SaveApiConnectionMode_CanCancelRestart_WhenSettingsAreDirty()
    {
        using var bootstrapFile = new BootstrapOverrideFileScope();
        var restartCount = 0;
        var vm = new DatabaseSettingsViewModel(
            bootstrapOptions: new DesktopBootstrapOptions
            {
                ConnectionMode = DesktopConnectionModes.LocalEmbedded,
                ApiBaseUrl = "http://localhost:5107"
            },
            restartApplication: () => restartCount++,
            confirmConnectionModeRestart: () => false,
            hasUnsavedSettingsEdits: () => true);

        vm.ApiConnectionMode = DesktopConnectionModes.Server;
        vm.ServerApiBaseUrl = "http://192.168.1.13:5107";

        await vm.SaveApiConnectionModeCommand.ExecuteAsync(null);

        Assert.Equal(0, restartCount);
        Assert.Contains("cancelled", vm.ApiConnectionStatus);
        Assert.Empty(DesktopBootstrapLocalOverrideStore.LoadConfigurationPairs());
    }


    [Fact]
    public void ServerMode_DisablesLocalEmbeddedDatabaseOverrideCommands()
    {
        var vm = new DatabaseSettingsViewModel(
            runtimeApi: new FakeRuntimeApiClient(),
            bootstrapOptions: new DesktopBootstrapOptions
            {
                ConnectionMode = DesktopConnectionModes.Server,
                ServerApiBaseUrl = "http://192.168.0.10:5107"
            });

        vm.DatabaseConnectionString = "Host=db;Database=showroom;Username=user;Password=secret";

        Assert.False(vm.IsDatabaseOverrideEditorEnabled);
        Assert.False(vm.TestDatabaseConnectionCommand.CanExecute(null));
        Assert.False(vm.SaveDatabaseConfigCommand.CanExecute(null));
    }

    [Fact]
    public void ServerMode_LabelsTheServerApiAsDatabaseOwner_AndLocalConfigAsFallback()
    {
        var vm = new DatabaseSettingsViewModel(
            runtimeApi: new FakeRuntimeApiClient(),
            bootstrapOptions: new DesktopBootstrapOptions
            {
                ConnectionMode = DesktopConnectionModes.Server,
                ServerApiBaseUrl = "http://192.168.0.10:5107"
            });

        Assert.Equal("Server API", vm.ActiveApiTitle);
        Assert.Equal("API (Server API)", vm.ActiveApiComponentText);
        Assert.Equal("ACTIVE", vm.ActiveApiStatusText);
        Assert.Equal("Owned by the server API", vm.ActiveDatabaseOwnerText);
        Assert.Equal("Configured from the server tray", vm.ActiveDatabaseSourceText);
        Assert.Equal("CONFIGURED", vm.ActiveDatabaseStatusText);
        Assert.False(vm.IsLocalDatabaseSectionVisible);
        Assert.Equal("Local fallback database", vm.LocalDatabaseSectionTitle);
        Assert.Equal("NOT IN USE", vm.LocalDatabaseUsageChipText);
        Assert.Contains("not the live server database", vm.LocalDatabaseSectionDescription);
    }

    [Fact]
    public async Task LocalMode_ReportsTheAppliedDatabaseConfigurationSource()
    {
        var runtime = new FakeRuntimeApiClient
        {
            DatabaseConfiguration = Response(canBootstrap: false, localOverride: true, requiresRestart: false)
        };
        var vm = new DatabaseSettingsViewModel(
            runtimeApi: runtime,
            bootstrapOptions: new DesktopBootstrapOptions
            {
                ConnectionMode = DesktopConnectionModes.LocalEmbedded,
                ApiBaseUrl = "http://localhost:5107"
            });

        await vm.LoadDatabaseConfigCommand.ExecuteAsync(null);

        Assert.Equal("Embedded API", vm.ActiveApiTitle);
        Assert.Equal("API (Embedded API)", vm.ActiveApiComponentText);
        Assert.Equal("ACTIVE", vm.ActiveApiStatusText);
        Assert.Equal("Owned by the embedded API", vm.ActiveDatabaseOwnerText);
        Assert.Equal("Encrypted local override", vm.ActiveDatabaseSourceText);
        Assert.Equal("Encrypted local override", vm.DatabaseSourceText);
        Assert.Equal("Development", vm.DatabaseEnvironmentName);
        Assert.True(vm.IsLocalDatabaseSectionVisible);
        Assert.Equal("ACTIVE PATH", vm.LocalDatabaseUsageChipText);
    }

    [Fact]
    public void CopyCommands_CopyOnlyTheEndpointAndConfigurationPath()
    {
        var copied = new List<string>();
        var vm = new DatabaseSettingsViewModel(
            bootstrapOptions: new DesktopBootstrapOptions
            {
                ConnectionMode = DesktopConnectionModes.LocalEmbedded,
                ApiBaseUrl = "http://localhost:5107"
            },
            copyToClipboard: copied.Add);
        const string secretConnection = "Host=db;Database=showroom;Username=user;Password=secret";
        const string configPath = @"C:\Users\operator\AppData\Roaming\ShowroomBilling\database.Production.local.json";
        vm.DatabaseConnectionString = secretConnection;
        vm.DatabaseConfigPath = configPath;

        vm.CopyLocalEmbeddedApiBaseUrlCommand.Execute(null);
        vm.CopyDatabaseConfigPathCommand.Execute(null);

        Assert.Equal(["http://localhost:5107", configPath], copied);
        Assert.DoesNotContain(secretConnection, copied);
        Assert.Equal("Copied to clipboard.", vm.DatabaseConfigStatus);
    }

    [Fact]
    public void AdminSidebarEntry_AppearsOnlyWhileUnlocked_AndFallsBackToDatabaseWhenRemoved()
    {
        var tokenStore = new AdminTokenStore();
        var settings = new SettingsViewModel(
            settingsApi: null,
            mastersApi: null,
            printAssetApi: null,
            printDispatcher: null,
            printPreferences: null,
            adminTokenStore: tokenStore);
        settings.AdminVm = new AdminUnlockViewModel(
            new StubAdminApiClient(),
            new StubDraftLeaseApiClient(),
            tokenStore);

        Assert.DoesNotContain(SettingsSectionKey.Admin, settings.Sections);

        tokenStore.Set(new AdminUnlockResponse(
            "admin-token",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(30),
            "admin"));

        Assert.Contains(SettingsSectionKey.Admin, settings.Sections);
        settings.SelectedSection = SettingsSectionKey.Admin;

        tokenStore.Clear();

        Assert.DoesNotContain(SettingsSectionKey.Admin, settings.Sections);
        Assert.Equal(SettingsSectionKey.Database, settings.SelectedSection);
    }

    [Fact]
    public void SwitchingBackToServer_RestoresLastNonLocalhostServerUrl()
    {
        var vm = new DatabaseSettingsViewModel(
            bootstrapOptions: new DesktopBootstrapOptions
            {
                ConnectionMode = DesktopConnectionModes.LocalEmbedded,
                ApiBaseUrl = "http://localhost:5107",
                ServerApiBaseUrl = "http://192.168.1.13:5107"
            });

        vm.ApiConnectionMode = DesktopConnectionModes.Server;
        Assert.Equal("http://192.168.1.13:5107", vm.ServerApiBaseUrl);
        Assert.False(vm.IsLocalDatabaseSectionVisible);

        vm.ApiConnectionMode = DesktopConnectionModes.LocalEmbedded;
        Assert.Contains("http://localhost:5107", vm.ApiConnectionStatus);
        Assert.True(vm.IsLocalDatabaseSectionVisible);

        vm.ApiConnectionMode = DesktopConnectionModes.Server;
        Assert.Equal("http://192.168.1.13:5107", vm.ServerApiBaseUrl);
        Assert.False(vm.IsLocalDatabaseSectionVisible);
    }

    [Fact]
    public async Task SaveDatabaseConfig_RequiresAdminUnlock_AndSendsToken()
    {
        var runtime = new FakeRuntimeApiClient();
        var tokenStore = new AdminTokenStore();
        var vm = new DatabaseSettingsViewModel(
            runtimeApi: runtime,
            adminTokenStore: tokenStore);

        var connectionString = "Host=db;Database=showroom;Username=user;Password=secret";
        vm.DatabaseConnectionString = connectionString;
        vm.AdminUnlockHandler = _ =>
        {
            tokenStore.Set(new AdminUnlockResponse(
                "admin-token",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddMinutes(30),
                "admin"));
            return Task.CompletedTask;
        };

        await vm.SaveDatabaseConfigCommand.ExecuteAsync(null);

        Assert.Equal("admin-token", runtime.LastAdminToken);
        Assert.Equal(connectionString, runtime.LastSavedConnectionString);
    }

    [Fact]
    public async Task SaveDatabaseConfig_UsesBootstrapWithoutAdmin_WhenBootstrapIsOpen()
    {
        var runtime = new FakeRuntimeApiClient
        {
            DatabaseConfiguration = Response(canBootstrap: true, localOverride: false, requiresRestart: false)
        };
        var tokenStore = new AdminTokenStore();
        var vm = new DatabaseSettingsViewModel(
            runtimeApi: runtime,
            adminTokenStore: tokenStore);

        await vm.LoadDatabaseConfigCommand.ExecuteAsync(null);
        var connectionString = "Host=db;Database=showroom;Username=user;Password=secret";
        vm.DatabaseConnectionString = connectionString;
        vm.AdminUnlockHandler = _ => throw new InvalidOperationException("Admin unlock should not run for first-run bootstrap.");

        await vm.SaveDatabaseConfigCommand.ExecuteAsync(null);

        Assert.Equal(connectionString, runtime.LastBootstrappedConnectionString);
        Assert.Null(runtime.LastAdminToken);
    }

    [Fact]
    public async Task SaveDatabaseConfig_RestartsManagedApi_AfterBootstrapSave()
    {
        var runtime = new FakeRuntimeApiClient
        {
            DatabaseConfiguration = Response(canBootstrap: true, localOverride: false, requiresRestart: false),
            BootstrapResponse = Response(canBootstrap: false, localOverride: true, requiresRestart: true)
        };
        var health = new FakeHealthApiClient(new SystemHealthSnapshot(
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
                true)));
        var supervisor = new FakeChildProcessSupervisor { CanRestartApi = true };
        var vm = new DatabaseSettingsViewModel(
            runtimeApi: runtime,
            healthApi: health,
            adminTokenStore: new AdminTokenStore(),
            childProcessSupervisor: supervisor);

        await vm.LoadDatabaseConfigCommand.ExecuteAsync(null);
        var connectionString = "Host=db;Database=showroom;Username=user;Password=secret";
        vm.DatabaseConnectionString = connectionString;

        await vm.SaveDatabaseConfigCommand.ExecuteAsync(null);

        Assert.Equal(1, supervisor.RestartCount);
        Assert.Contains("database is ready", vm.DatabaseConfigStatus);
    }

    [Fact]
    public async Task SettingsViewModel_BridgesAdminUnlockHandler_AndDatabaseLoad()
    {
        var runtime = new FakeRuntimeApiClient();
        Func<CancellationToken, Task> handler = _ => Task.CompletedTask;
        var vm = new SettingsViewModel(
            settingsApi: null,
            mastersApi: null,
            printAssetApi: null,
            printDispatcher: null,
            printPreferences: null,
            runtimeApi: runtime);

        vm.AdminUnlockHandler = handler;
        await vm.LoadDatabaseConfigAsync();

        Assert.Same(handler, vm.Database.AdminUnlockHandler);
        Assert.Contains("loaded", vm.Database.DatabaseConfigStatus, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FakeRuntimeApiClient : IRuntimeApiClient
    {
        public DatabaseConfigurationResponse DatabaseConfiguration { get; set; } =
            Response(canBootstrap: false, localOverride: true, requiresRestart: false);

        public DatabaseConfigurationResponse? BootstrapResponse { get; set; }

        public string? LastAdminToken { get; private set; }

        public string? LastSavedConnectionString { get; private set; }

        public string? LastBootstrappedConnectionString { get; private set; }

        public Task<RuntimeBootstrapResponse> GetBootstrapAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DatabaseConfigurationResponse> GetDatabaseConfigurationAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(DatabaseConfiguration);

        public Task<DatabaseConfigurationTestResponse> TestDatabaseConfigurationAsync(
            TestDatabaseConfigurationRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DatabaseConfigurationTestResponse(true, "Connection succeeded."));

        public Task<DatabaseConfigurationResponse> UpdateDatabaseConfigurationAsync(
            UpdateDatabaseConfigurationRequest request,
            string adminToken,
            CancellationToken cancellationToken = default)
        {
            LastAdminToken = adminToken;
            LastSavedConnectionString = request.ConnectionString;
            return Task.FromResult(new DatabaseConfigurationResponse(
                "PostgreSQL",
                string.Empty,
                "Host=db;Database=showroom;Username=user;Password=***",
                "database.Development.local.json",
                true,
                true));
        }

        public Task<DatabaseConfigurationResponse> BootstrapDatabaseConfigurationAsync(
            UpdateDatabaseConfigurationRequest request,
            CancellationToken cancellationToken = default)
        {
            LastBootstrappedConnectionString = request.ConnectionString;
            DatabaseConfiguration = BootstrapResponse ?? Response(
                canBootstrap: false,
                localOverride: true,
                requiresRestart: true);
            return Task.FromResult(DatabaseConfiguration);
        }
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

    private sealed class FakeHealthApiClient(SystemHealthSnapshot snapshot) : IHealthApiClient
    {
        public Task<SystemHealthSnapshot> GetSnapshotAsync(
            bool includeTallyCompany,
            bool includeMasterFreshness = true,
            bool forceDatabaseHealth = false,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(snapshot);
    }

    private sealed class StubAdminApiClient : IAdminApiClient
    {
        public Task<AdminPasscodeStatusResponse> GetPasscodeStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AdminPasscodeStatusResponse(true));

        public Task SetPasscodeAsync(AdminSetPasscodeRequest request, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<AdminUnlockResponse> UnlockAsync(AdminUnlockRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task LogoutAsync(AdminLogoutRequest request, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class StubDraftLeaseApiClient : IDraftLeaseApiClient
    {
        public Task<DraftLeaseAcquireResult> AcquireAsync(DraftLeaseAcquireRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DraftLeaseResponse> RenewAsync(Guid leaseId, DraftLeaseRenewRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DraftLeaseResponse> ReleaseAsync(Guid leaseId, DraftLeaseReleaseRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DraftLeaseResponse?> GetActiveForBillAsync(Guid billId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DraftLeaseListResponse> ListActiveAsync(string adminToken, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DraftLeaseListResponse(Array.Empty<DraftLeaseResponse>()));

        public Task<DraftLeaseResponse> ForceReleaseAsync(
            Guid leaseId,
            DraftLeaseForceReleaseRequest request,
            string adminToken,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private static DatabaseConfigurationResponse Response(
        bool canBootstrap,
        bool localOverride,
        bool requiresRestart) =>
        new(
            "PostgreSQL",
            string.Empty,
            "Host=db;Database=showroom;Username=user;Password=***",
            "database.Development.local.json",
            localOverride,
            requiresRestart,
            "Development",
            false,
            "Windows DPAPI CurrentUser",
            canBootstrap);

    private sealed class BootstrapOverrideFileScope : IDisposable
    {
        private readonly string _path = DesktopBootstrapLocalOverrideStore.ConfigPath;
        private readonly bool _existed;
        private readonly string? _backup;

        public BootstrapOverrideFileScope()
        {
            if (File.Exists(_path))
            {
                _existed = true;
                _backup = File.ReadAllText(_path);
                File.Delete(_path);
            }
        }

        public void Dispose()
        {
            if (_existed)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, _backup);
                return;
            }

            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
    }
}
