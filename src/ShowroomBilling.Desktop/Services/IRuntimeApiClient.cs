using ShowroomBilling.Contracts.Runtime;

namespace ShowroomBilling.Desktop.Services;

public interface IRuntimeApiClient
{
    Task<RuntimeBootstrapResponse> GetBootstrapAsync(CancellationToken cancellationToken = default);

    Task<DatabaseConfigurationResponse> GetDatabaseConfigurationAsync(CancellationToken cancellationToken = default);

    Task<DatabaseConfigurationTestResponse> TestDatabaseConfigurationAsync(
        TestDatabaseConfigurationRequest request,
        CancellationToken cancellationToken = default);

    Task<DatabaseConfigurationResponse> UpdateDatabaseConfigurationAsync(
        UpdateDatabaseConfigurationRequest request,
        string adminToken,
        CancellationToken cancellationToken = default);

    Task<DatabaseConfigurationResponse> BootstrapDatabaseConfigurationAsync(
        UpdateDatabaseConfigurationRequest request,
        CancellationToken cancellationToken = default);
}
