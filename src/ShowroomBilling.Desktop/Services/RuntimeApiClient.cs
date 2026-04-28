using System.Net.Http;
using System.Net.Http.Json;
using ShowroomBilling.Contracts.Admin;
using ShowroomBilling.Contracts.Runtime;

namespace ShowroomBilling.Desktop.Services;

public sealed class RuntimeApiClient(HttpClient httpClient) : IRuntimeApiClient
{
    public async Task<RuntimeBootstrapResponse> GetBootstrapAsync(CancellationToken cancellationToken = default)
    {
        var response = await httpClient.GetFromJsonAsync<RuntimeBootstrapResponse>("/api/runtime/bootstrap", cancellationToken);
        return response ?? throw new InvalidOperationException("API returned an empty runtime bootstrap payload.");
    }

    public async Task<DatabaseConfigurationResponse> GetDatabaseConfigurationAsync(CancellationToken cancellationToken = default)
    {
        var response = await httpClient.GetFromJsonAsync<DatabaseConfigurationResponse>("/api/runtime/database", cancellationToken);
        return response ?? throw new InvalidOperationException("API returned an empty database configuration payload.");
    }

    public async Task<DatabaseConfigurationTestResponse> TestDatabaseConfigurationAsync(
        TestDatabaseConfigurationRequest request,
        CancellationToken cancellationToken = default)
    {
        var http = await httpClient.PostAsJsonAsync("/api/runtime/database/test", request, cancellationToken);
        await ApiResponseReader.EnsureSuccessOrThrowAsync(http, cancellationToken);
        var response = await http.Content.ReadFromJsonAsync<DatabaseConfigurationTestResponse>(cancellationToken: cancellationToken);
        return response ?? throw new InvalidOperationException("API returned an empty database test payload.");
    }

    public async Task<DatabaseConfigurationResponse> UpdateDatabaseConfigurationAsync(
        UpdateDatabaseConfigurationRequest request,
        string adminToken,
        CancellationToken cancellationToken = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Put, "/api/runtime/database")
        {
            Content = JsonContent.Create(request)
        };
        message.Headers.Add(AdminTokenConstants.HeaderName, adminToken);
        var http = await httpClient.SendAsync(message, cancellationToken);
        await ApiResponseReader.EnsureSuccessOrThrowAsync(http, cancellationToken);
        var response = await http.Content.ReadFromJsonAsync<DatabaseConfigurationResponse>(cancellationToken: cancellationToken);
        return response ?? throw new InvalidOperationException("API returned an empty database configuration update payload.");
    }

    public async Task<DatabaseConfigurationResponse> BootstrapDatabaseConfigurationAsync(
        UpdateDatabaseConfigurationRequest request,
        CancellationToken cancellationToken = default)
    {
        var http = await httpClient.PutAsJsonAsync("/api/runtime/database/bootstrap", request, cancellationToken);
        await ApiResponseReader.EnsureSuccessOrThrowAsync(http, cancellationToken);
        var response = await http.Content.ReadFromJsonAsync<DatabaseConfigurationResponse>(cancellationToken: cancellationToken);
        return response ?? throw new InvalidOperationException("API returned an empty database bootstrap payload.");
    }
}
