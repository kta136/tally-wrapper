using System.Net.Http;
using ShowroomBilling.Contracts.Settings;

namespace ShowroomBilling.Desktop.Services;

public sealed class SettingsApiClient(HttpClient httpClient) : ISettingsApiClient
{
    public Task<EffectiveSettingsResponse> GetEffectiveSettingsAsync(CancellationToken cancellationToken = default)
        => httpClient.GetApiAsync<EffectiveSettingsResponse>("/api/settings", cancellationToken);

    public Task<SettingsUpdateResponse> SaveEffectiveSettingsAsync(
        UpdateEffectiveSettingsRequest request,
        CancellationToken cancellationToken = default)
        => httpClient.PutApiAsync<SettingsUpdateResponse>("/api/settings", request, cancellationToken);

    public Task<SettingsUpdateResponse> SelectActiveCompanyAsync(
        SelectActiveCompanyRequest request,
        CancellationToken cancellationToken = default)
        => httpClient.PostApiAsync<SettingsUpdateResponse>("/api/settings/company/select", request, cancellationToken);

    public Task<PrintLayoutResponse> GetPrintLayoutAsync(CancellationToken cancellationToken = default)
        => httpClient.GetApiAsync<PrintLayoutResponse>("/api/settings/print-layout", cancellationToken);

    public Task<PrintLayoutResponse> UpdatePrintLayoutAsync(
        UpdatePrintLayoutRequest request,
        CancellationToken cancellationToken = default)
        => httpClient.PutApiAsync<PrintLayoutResponse>("/api/settings/print-layout", request, cancellationToken);
}
