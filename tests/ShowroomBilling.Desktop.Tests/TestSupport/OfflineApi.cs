using System.Net.Http;
using ShowroomBilling.Contracts.Masters;
using ShowroomBilling.Contracts.PrintAssets;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Contracts.Tally;
using ShowroomBilling.Desktop.Configuration;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.ViewModels;
using ShowroomBilling.Desktop.ViewModels.Settings;

namespace ShowroomBilling.Desktop.Tests.TestSupport;

/// <summary>
/// API clients for a test that is not about the server: every call fails the
/// way the real client does when the server cannot be reached. A view model
/// always has its clients, so a test that needs none of their answers passes
/// these.
/// </summary>
internal static class OfflineApi
{
    public const string Message = "No connection could be made (this test has no server).";

    /// <summary>A settings view model with no server behind it: what a test fills in by hand stays as it is.</summary>
    public static SettingsViewModel Settings(
        ISettingsApiClient? settingsApi = null,
        DesktopBootstrapOptions? bootstrapOptions = null,
        HandoverKeyStore? handoverKeys = null,
        IDialogService? dialogs = null,
        ITallyCheckApiClient? tallyCheckApi = null)
        => new(
            settingsApi ?? new OfflineSettingsApi(),
            new OfflineMastersApi(),
            new OfflinePrintAssetApi(),
            tallyCheckApi ?? new OfflineTallyCheckApi(),
            dialogs ?? new FakeDialogs(),
            bootstrapOptions: bootstrapOptions,
            handoverKeys: handoverKeys);

    internal static Task<T> Fail<T>() => Task.FromException<T>(new HttpRequestException(Message));
}

internal sealed class OfflineSettingsApi : ISettingsApiClient
{
    public Task<EffectiveSettingsResponse> GetEffectiveSettingsAsync(CancellationToken cancellationToken = default)
        => OfflineApi.Fail<EffectiveSettingsResponse>();

    public Task<SettingsUpdateResponse> SaveEffectiveSettingsAsync(
        UpdateEffectiveSettingsRequest request,
        CancellationToken cancellationToken = default)
        => OfflineApi.Fail<SettingsUpdateResponse>();

    public Task<SettingsUpdateResponse> SelectActiveCompanyAsync(
        SelectActiveCompanyRequest request,
        CancellationToken cancellationToken = default)
        => OfflineApi.Fail<SettingsUpdateResponse>();

    public Task<PrintLayoutResponse> GetPrintLayoutAsync(CancellationToken cancellationToken = default)
        => OfflineApi.Fail<PrintLayoutResponse>();

    public Task<PrintLayoutResponse> UpdatePrintLayoutAsync(
        UpdatePrintLayoutRequest request,
        CancellationToken cancellationToken = default)
        => OfflineApi.Fail<PrintLayoutResponse>();
}

internal sealed class OfflineMastersApi : IMastersApiClient
{
    public Task<CompanySnapshotResponse> GetCompaniesAsync(CancellationToken cancellationToken = default)
        => OfflineApi.Fail<CompanySnapshotResponse>();

    public Task<LedgerSnapshotResponse> GetLedgersAsync(CancellationToken cancellationToken = default)
        => OfflineApi.Fail<LedgerSnapshotResponse>();

    public Task<VoucherTypeSnapshotResponse> GetVoucherTypesAsync(CancellationToken cancellationToken = default)
        => OfflineApi.Fail<VoucherTypeSnapshotResponse>();

    public Task<StockItemSnapshotResponse> GetStockItemsAsync(CancellationToken cancellationToken = default)
        => OfflineApi.Fail<StockItemSnapshotResponse>();

    public Task<IReadOnlyList<TallyMasterRefreshResult>> RequestRefreshAsync(
        MasterRefreshRequest request,
        CancellationToken cancellationToken = default)
        => OfflineApi.Fail<IReadOnlyList<TallyMasterRefreshResult>>();
}

internal sealed class OfflineTallyCheckApi : ITallyCheckApiClient
{
    public Task<TallyComparisonResponse> CompareAsync(string? fiscalYear = null, CancellationToken cancellationToken = default)
        => OfflineApi.Fail<TallyComparisonResponse>();
}

internal sealed class OfflinePrintAssetApi : IPrintAssetApiClient
{
    public Task<PrintAssetListResponse> ListAsync(CancellationToken cancellationToken = default)
        => OfflineApi.Fail<PrintAssetListResponse>();

    public Task<PrintAssetResponse> UploadAsync(PrintAssetUploadRequest request, CancellationToken cancellationToken = default)
        => OfflineApi.Fail<PrintAssetResponse>();

    public Task<byte[]?> DownloadAsync(Guid id, CancellationToken cancellationToken = default)
        => OfflineApi.Fail<byte[]?>();

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        => OfflineApi.Fail<bool>();
}
