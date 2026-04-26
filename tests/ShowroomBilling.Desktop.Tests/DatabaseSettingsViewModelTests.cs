using ShowroomBilling.Contracts.Admin;
using ShowroomBilling.Contracts.Runtime;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.ViewModels.Settings;

namespace ShowroomBilling.Desktop.Tests;

public sealed class DatabaseSettingsViewModelTests
{
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

        vm.DatabaseConnectionString = "Host=db;Database=showroom;Username=user;Password=secret";
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
        Assert.Equal(vm.DatabaseConnectionString, runtime.LastSavedConnectionString);
    }

    private sealed class FakeRuntimeApiClient : IRuntimeApiClient
    {
        public string? LastAdminToken { get; private set; }

        public string? LastSavedConnectionString { get; private set; }

        public Task<RuntimeBootstrapResponse> GetBootstrapAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DatabaseConfigurationResponse> GetDatabaseConfigurationAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new DatabaseConfigurationResponse(
                "PostgreSQL",
                "Host=db;Database=showroom;Username=user;Password=secret",
                "Host=db;Database=showroom;Username=user;Password=***",
                "database.Development.local.json",
                true,
                false));

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
                request.ConnectionString,
                "Host=db;Database=showroom;Username=user;Password=***",
                "database.Development.local.json",
                true,
                true));
        }
    }
}
