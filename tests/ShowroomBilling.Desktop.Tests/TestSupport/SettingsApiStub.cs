using ShowroomBilling.Contracts.Settings;

using ShowroomBilling.Desktop.Services;

namespace ShowroomBilling.Desktop.Tests.TestSupport;

internal abstract class SettingsApiStub : ISettingsApiClient
{
    public virtual Task<EffectiveSettingsResponse> GetEffectiveSettingsAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public virtual Task<SettingsUpdateResponse> SaveEffectiveSettingsAsync(
        UpdateEffectiveSettingsRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public virtual Task<SettingsUpdateResponse> SelectActiveCompanyAsync(
        SelectActiveCompanyRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public virtual Task<PrintLayoutResponse> GetPrintLayoutAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public virtual Task<PrintLayoutResponse> UpdatePrintLayoutAsync(
        UpdatePrintLayoutRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
