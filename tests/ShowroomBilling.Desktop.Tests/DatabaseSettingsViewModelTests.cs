using System.IO;
using ShowroomBilling.Contracts.Admin;
using ShowroomBilling.Contracts.Masters;
using ShowroomBilling.Contracts.Runtime;
using ShowroomBilling.Desktop.Configuration;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.Services.ProcessSupervision;
using ShowroomBilling.Desktop.ViewModels.Settings;

namespace ShowroomBilling.Desktop.Tests;

public sealed class DatabaseSettingsViewModelTests
{
    [Fact]
    public async Task SaveApiConnectionMode_WritesBootstrapOverride_AndRestarts()
    {
        using var bootstrapFile = new BootstrapOverrideFileScope();
        var restartCount = 0;
        var vm = new SettingsViewModel(
            settingsApi: null,
            mastersApi: null,
            printAssetApi: null,
            printDispatcher: null,
            printPreferences: null,
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
        var vm = new SettingsViewModel(
            settingsApi: null,
            mastersApi: null,
            printAssetApi: null,
            printDispatcher: null,
            printPreferences: null,
            bootstrapOptions: new DesktopBootstrapOptions
            {
                ConnectionMode = DesktopConnectionModes.LocalEmbedded,
                ApiBaseUrl = "http://localhost:5107"
            },
            restartApplication: () => restartCount++,
            confirmConnectionModeRestart: () => false);

        vm.IsDirty = true;
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
        var vm = new SettingsViewModel(
            settingsApi: null,
            mastersApi: null,
            printAssetApi: null,
            printDispatcher: null,
            printPreferences: null,
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
    public void SwitchingBackToServer_RestoresLastNonLocalhostServerUrl()
    {
        var vm = new SettingsViewModel(
            settingsApi: null,
            mastersApi: null,
            printAssetApi: null,
            printDispatcher: null,
            printPreferences: null,
            bootstrapOptions: new DesktopBootstrapOptions
            {
                ConnectionMode = DesktopConnectionModes.LocalEmbedded,
                ApiBaseUrl = "http://localhost:5107",
                ServerApiBaseUrl = "http://192.168.1.13:5107"
            });

        vm.ApiConnectionMode = DesktopConnectionModes.Server;
        Assert.Equal("http://192.168.1.13:5107", vm.ServerApiBaseUrl);

        vm.ApiConnectionMode = DesktopConnectionModes.LocalEmbedded;
        Assert.Contains("http://localhost:5107", vm.ApiConnectionStatus);

        vm.ApiConnectionMode = DesktopConnectionModes.Server;
        Assert.Equal("http://192.168.1.13:5107", vm.ServerApiBaseUrl);
    }

    [Fact]
    public async Task SaveDatabaseConfig_RequiresAdminUnlock_AndSendsToken()
    {
        var runtime = new FakeRuntimeApiClient();
        var tokenStore = new AdminTokenStore();
        var vm = new SettingsViewModel(
            settingsApi: null,
            mastersApi: null,
            printAssetApi: null,
            printDispatcher: null,
            printPreferences: null,
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
        var vm = new SettingsViewModel(
            settingsApi: null,
            mastersApi: null,
            printAssetApi: null,
            printDispatcher: null,
            printPreferences: null,
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
        var vm = new SettingsViewModel(
            settingsApi: null,
            mastersApi: null,
            printAssetApi: null,
            printDispatcher: null,
            printPreferences: null,
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
            CancellationToken cancellationToken = default) =>
            Task.FromResult(snapshot);
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
