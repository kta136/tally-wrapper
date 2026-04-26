using System.Net.Http;
using System.Net.Http.Json;
using ShowroomBilling.Contracts.Settings;

namespace ShowroomBilling.Desktop.Services;

public sealed class SettingsApiClient(HttpClient httpClient) : ISettingsApiClient
{
    public async Task<EffectiveSettingsResponse> GetEffectiveSettingsAsync(CancellationToken cancellationToken = default)
    {
        var response = await httpClient.GetFromJsonAsync<EffectiveSettingsResponse>("/api/settings", cancellationToken);
        return response ?? throw new InvalidOperationException("API returned an empty settings payload.");
    }

    public async Task<SettingsUpdateResponse> SaveEffectiveSettingsAsync(
        UpdateEffectiveSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        var http = await httpClient.PutAsJsonAsync("/api/settings", request, cancellationToken);
        await ApiResponseReader.EnsureSuccessOrThrowAsync(http, cancellationToken);
        var response = await http.Content.ReadFromJsonAsync<SettingsUpdateResponse>(cancellationToken: cancellationToken);
        return response ?? throw new InvalidOperationException("API returned an empty save response.");
    }

    public async Task<SettingsUpdateResponse> SelectActiveCompanyAsync(
        SelectActiveCompanyRequest request,
        CancellationToken cancellationToken = default)
    {
        var http = await httpClient.PostAsJsonAsync("/api/settings/company/select", request, cancellationToken);
        await ApiResponseReader.EnsureSuccessOrThrowAsync(http, cancellationToken);
        var response = await http.Content.ReadFromJsonAsync<SettingsUpdateResponse>(cancellationToken: cancellationToken);
        return response ?? throw new InvalidOperationException("API returned an empty company-select response.");
    }

    public async Task<PrintLayoutResponse> GetPrintLayoutAsync(CancellationToken cancellationToken = default)
    {
        var response = await httpClient.GetFromJsonAsync<PrintLayoutResponse>("/api/settings/print-layout", cancellationToken);
        return response ?? throw new InvalidOperationException("API returned an empty print-layout payload.");
    }

    public async Task<PrintLayoutResponse> UpdatePrintLayoutAsync(
        UpdatePrintLayoutRequest request,
        CancellationToken cancellationToken = default)
    {
        var http = await httpClient.PutAsJsonAsync("/api/settings/print-layout", request, cancellationToken);
        await ApiResponseReader.EnsureSuccessOrThrowAsync(http, cancellationToken);
        var response = await http.Content.ReadFromJsonAsync<PrintLayoutResponse>(cancellationToken: cancellationToken);
        return response ?? throw new InvalidOperationException("API returned an empty print-layout update response.");
    }
}
