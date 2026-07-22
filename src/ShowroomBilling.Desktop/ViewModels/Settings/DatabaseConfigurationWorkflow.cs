using System.Net.Http;
using ShowroomBilling.Contracts.Admin;
using ShowroomBilling.Contracts.Runtime;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.Services.ProcessSupervision;

namespace ShowroomBilling.Desktop.ViewModels.Settings;

internal interface IDatabaseConfigurationWorkflowHost
{
    string DatabaseConnectionString { get; set; }
    string DatabaseMaskedConnectionString { get; set; }
    string DatabaseConfigPath { get; set; }
    string DatabaseConfigStatus { get; set; }
    string DatabaseProvider { get; set; }
    string DatabaseEnvironmentName { get; set; }
    string DatabaseStorageProtection { get; set; }
    bool IsDatabaseConfigBusy { get; set; }
    bool IsTestingDatabaseConnection { get; set; }
    bool IsSavingDatabaseConfig { get; set; }
    bool IsRestartingApi { get; set; }
    bool IsLocalDatabaseOverridePresent { get; set; }
    bool IsEnvironmentDatabaseOverridePresent { get; set; }
    bool DatabaseConfigRequiresRestart { get; set; }
    bool CanBootstrapDatabaseWithoutAdmin { get; set; }
    Func<CancellationToken, Task>? AdminUnlockHandler { get; }
}

internal sealed class DatabaseConfigurationWorkflow(
    IRuntimeApiClient? runtimeApi,
    IHealthApiClient? healthApi,
    AdminTokenStore? adminTokenStore,
    IChildProcessSupervisor? childProcessSupervisor,
    IDatabaseConfigurationWorkflowHost host)
{
    private static readonly TimeSpan DatabaseReadyTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DatabaseReadyPollInterval = TimeSpan.FromMilliseconds(500);

    public bool CanUseCommands() =>
        runtimeApi is not null
        && !host.IsDatabaseConfigBusy
        && !host.IsTestingDatabaseConnection
        && !host.IsSavingDatabaseConfig
        && !string.IsNullOrWhiteSpace(host.DatabaseConnectionString);

    public bool CanRestartApi => childProcessSupervisor?.CanRestartApi == true && !host.IsRestartingApi;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (runtimeApi is null)
        {
            host.DatabaseConfigStatus = "Runtime API unavailable.";
            return;
        }

        host.IsDatabaseConfigBusy = true;
        host.DatabaseConfigStatus = "Loading database configuration…";
        try
        {
            var response = await runtimeApi.GetDatabaseConfigurationAsync(cancellationToken);
            Apply(response);
            host.DatabaseConfigStatus = response.RequiresApiRestart
                ? "Database configuration loaded. Restart the API or desktop app to apply the saved override."
                : "Database configuration loaded.";
        }
        catch (HttpRequestException ex)
        {
            host.DatabaseConfigStatus = $"Load failed: {ApiResponseReader.FormatError(ex)}";
        }
        catch (Exception ex)
        {
            host.DatabaseConfigStatus = $"Load failed: {ex.Message}";
        }
        finally
        {
            host.IsDatabaseConfigBusy = false;
        }
    }

    public async Task TestAsync(CancellationToken cancellationToken)
    {
        if (runtimeApi is null) return;

        host.IsTestingDatabaseConnection = true;
        host.DatabaseConfigStatus = "Testing database connection…";
        try
        {
            var response = await runtimeApi.TestDatabaseConfigurationAsync(
                new TestDatabaseConfigurationRequest(host.DatabaseConnectionString),
                cancellationToken);
            host.DatabaseConfigStatus = response.Message;
        }
        catch (HttpRequestException ex)
        {
            host.DatabaseConfigStatus = $"Test failed: {ApiResponseReader.FormatError(ex)}";
        }
        catch (Exception ex)
        {
            host.DatabaseConfigStatus = $"Test failed: {ex.Message}";
        }
        finally
        {
            host.IsTestingDatabaseConnection = false;
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (runtimeApi is null) return;

        host.IsSavingDatabaseConfig = true;
        host.DatabaseConfigStatus = host.CanBootstrapDatabaseWithoutAdmin
            ? "Saving first-run database configuration…"
            : "Saving database configuration…";
        try
        {
            DatabaseConfigurationResponse response;
            if (host.CanBootstrapDatabaseWithoutAdmin)
            {
                response = await runtimeApi.BootstrapDatabaseConfigurationAsync(
                    new UpdateDatabaseConfigurationRequest(host.DatabaseConnectionString),
                    cancellationToken);
            }
            else
            {
                var token = adminTokenStore?.Current?.Token;
                if (string.IsNullOrWhiteSpace(token) && host.AdminUnlockHandler is not null)
                {
                    host.DatabaseConfigStatus = string.Empty;
                    host.IsSavingDatabaseConfig = false;
                    await host.AdminUnlockHandler(cancellationToken);
                    host.IsSavingDatabaseConfig = true;
                    token = adminTokenStore?.Current?.Token;
                    host.DatabaseConfigStatus = "Saving database configuration…";
                }

                if (string.IsNullOrWhiteSpace(token))
                {
                    host.DatabaseConfigStatus = "Save cancelled.";
                    return;
                }

                response = await runtimeApi.UpdateDatabaseConfigurationAsync(
                    new UpdateDatabaseConfigurationRequest(host.DatabaseConnectionString),
                    token,
                    cancellationToken);
            }

            Apply(response);
            if (response.RequiresApiRestart && childProcessSupervisor?.CanRestartApi == true)
            {
                await RestartApiCoreAsync(waitForDatabaseReady: true, cancellationToken);
                return;
            }

            host.DatabaseConfigStatus = response.RequiresApiRestart
                ? "Saved. Restart the API or desktop app for the new database to be used."
                : "Saved. Database is ready.";
        }
        catch (HttpRequestException ex)
        {
            host.DatabaseConfigStatus = $"Save failed: {ApiResponseReader.FormatError(ex)}";
        }
        catch (Exception ex)
        {
            host.DatabaseConfigStatus = $"Save failed: {ex.Message}";
        }
        finally
        {
            host.IsSavingDatabaseConfig = false;
        }
    }

    public async Task RestartApiAsync(CancellationToken cancellationToken)
        => await RestartApiCoreAsync(waitForDatabaseReady: false, cancellationToken);

    private async Task RestartApiCoreAsync(bool waitForDatabaseReady, CancellationToken cancellationToken)
    {
        if (childProcessSupervisor?.CanRestartApi != true)
        {
            host.DatabaseConfigStatus = "Desktop is not managing the API process in this run. Restart the API manually.";
            return;
        }

        host.IsRestartingApi = true;
        host.DatabaseConfigStatus = "Restarting API…";
        try
        {
            var restarted = await Task.Run(() => childProcessSupervisor.RestartApi(), cancellationToken);
            host.DatabaseConfigStatus = restarted
                ? "API restarted. Rechecking database configuration…"
                : "API restart was requested but no managed API child started.";

            var databaseReady = false;
            if (restarted && waitForDatabaseReady)
            {
                databaseReady = await WaitForDatabaseReadyAsync(cancellationToken);
            }

            await LoadAsync(cancellationToken);
            if (restarted && waitForDatabaseReady)
            {
                host.DatabaseConfigStatus = databaseReady
                    ? "Saved. API restarted and database is ready."
                    : "Saved and API restarted, but database is not ready yet. Check the connection string and runtime health.";
            }
        }
        catch (OperationCanceledException)
        {
            host.DatabaseConfigStatus = "API restart cancelled.";
        }
        catch (Exception ex)
        {
            host.DatabaseConfigStatus = $"API restart failed: {ex.Message}";
        }
        finally
        {
            host.IsRestartingApi = false;
        }
    }

    private async Task<bool> WaitForDatabaseReadyAsync(CancellationToken cancellationToken)
    {
        if (healthApi is null)
        {
            return false;
        }

        var deadline = DateTimeOffset.UtcNow + DatabaseReadyTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var snapshot = await healthApi.GetSnapshotAsync(
                includeTallyCompany: false,
                includeMasterFreshness: false,
                forceDatabaseHealth: true,
                cancellationToken);
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

    private void Apply(DatabaseConfigurationResponse response)
    {
        host.DatabaseConnectionString = response.ConnectionString;
        host.DatabaseMaskedConnectionString = string.IsNullOrWhiteSpace(response.MaskedConnectionString)
            ? "—"
            : response.MaskedConnectionString;
        host.DatabaseConfigPath = response.ConfigPath;
        host.DatabaseProvider = string.IsNullOrWhiteSpace(response.Provider)
            ? "PostgreSQL"
            : response.Provider;
        host.DatabaseEnvironmentName = string.IsNullOrWhiteSpace(response.EnvironmentName)
            ? "—"
            : response.EnvironmentName;
        host.DatabaseStorageProtection = string.IsNullOrWhiteSpace(response.StorageProtection)
            ? "—"
            : response.StorageProtection;
        host.IsLocalDatabaseOverridePresent = response.IsLocalOverridePresent;
        host.IsEnvironmentDatabaseOverridePresent = response.IsEnvironmentOverridePresent;
        host.DatabaseConfigRequiresRestart = response.RequiresApiRestart;
        host.CanBootstrapDatabaseWithoutAdmin = response.CanBootstrapWithoutAdmin;
    }
}
