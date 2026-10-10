using System.Net.Http;
using ShowroomBilling.Contracts.Runtime;

namespace ShowroomBilling.Desktop.Services;

public sealed class RuntimeApiClient(HttpClient httpClient) : IRuntimeApiClient
{
    public Task<RuntimeBootstrapResponse> GetBootstrapAsync(CancellationToken cancellationToken = default)
        => httpClient.GetApiAsync<RuntimeBootstrapResponse>("/api/runtime/bootstrap", cancellationToken);

    public Task<DatabaseConfigurationResponse> GetDatabaseConfigurationAsync(CancellationToken cancellationToken = default)
        => httpClient.GetApiAsync<DatabaseConfigurationResponse>("/api/runtime/database", cancellationToken);

    public Task<DatabaseConfigurationTestResponse> TestDatabaseConfigurationAsync(
        TestDatabaseConfigurationRequest request,
        CancellationToken cancellationToken = default)
        => httpClient.PostApiAsync<DatabaseConfigurationTestResponse>("/api/runtime/database/test", request, cancellationToken);

    public Task<DatabaseConfigurationResponse> UpdateDatabaseConfigurationAsync(
        UpdateDatabaseConfigurationRequest request,
        string adminToken,
        CancellationToken cancellationToken = default)
        => httpClient.PutApiAsync<DatabaseConfigurationResponse>("/api/runtime/database", request, cancellationToken, adminToken);

    public Task<DatabaseConfigurationResponse> BootstrapDatabaseConfigurationAsync(
        UpdateDatabaseConfigurationRequest request,
        CancellationToken cancellationToken = default)
        => httpClient.PutApiAsync<DatabaseConfigurationResponse>("/api/runtime/database/bootstrap", request, cancellationToken);
}
