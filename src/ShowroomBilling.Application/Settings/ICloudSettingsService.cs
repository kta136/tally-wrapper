using ShowroomBilling.Contracts.Settings;

namespace ShowroomBilling.Application.Settings;

public interface ICloudSettingsService
{
    Task<EffectiveSettingsResponse> GetEffectiveSettingsAsync(CancellationToken cancellationToken = default);

    Task<SettingsUpdateResponse> SaveEffectiveSettingsAsync(
        UpdateEffectiveSettingsRequest request,
        CancellationToken cancellationToken = default);

    Task<SettingsUpdateResponse> SelectActiveCompanyAsync(
        string companyName,
        CancellationToken cancellationToken = default);

    Task<PrintLayoutResponse> GetPrintLayoutAsync(CancellationToken cancellationToken = default);

    Task<PrintLayoutResponse> UpdatePrintLayoutAsync(
        UpdatePrintLayoutRequest request,
        CancellationToken cancellationToken = default);
}
