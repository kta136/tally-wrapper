using ShowroomBilling.Contracts.Settings;

namespace ShowroomBilling.Desktop.Services;

public interface ISettingsApiClient
{
    Task<EffectiveSettingsResponse> GetEffectiveSettingsAsync(CancellationToken cancellationToken = default);

    Task<SettingsUpdateResponse> SaveEffectiveSettingsAsync(
        UpdateEffectiveSettingsRequest request,
        CancellationToken cancellationToken = default);

    Task<SettingsUpdateResponse> SelectActiveCompanyAsync(
        SelectActiveCompanyRequest request,
        CancellationToken cancellationToken = default);

    Task<PrintLayoutResponse> GetPrintLayoutAsync(CancellationToken cancellationToken = default);

    Task<PrintLayoutResponse> UpdatePrintLayoutAsync(
        UpdatePrintLayoutRequest request,
        CancellationToken cancellationToken = default);
}
