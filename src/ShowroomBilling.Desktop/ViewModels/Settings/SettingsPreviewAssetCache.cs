using ShowroomBilling.Desktop.Services;

namespace ShowroomBilling.Desktop.ViewModels.Settings;

internal sealed class SettingsPreviewAssetCache(
    IPrintAssetApiClient? assets,
    PrintLayoutViewModel? printLayout,
    Action enqueueRefresh)
{
    private int _generation;
    private Guid? _loadedLogoId;
    private Guid? _loadedSignatureId;
    private Guid? _loadedWatermarkId;

    internal byte[]? ServerLogoBytes { get; private set; }
    internal byte[]? ServerSignatureBytes { get; private set; }
    internal byte[]? ServerWatermarkBytes { get; private set; }

    internal void IncrementGeneration()
    {
        Interlocked.Increment(ref _generation);
    }

    internal async Task EnsureServerAssetsAsync()
    {
        if (assets is null || printLayout is null) return;

        var generation = Volatile.Read(ref _generation);
        var logoId = printLayout.LogoAssetId;
        var signatureId = printLayout.SignatureAssetId;
        var watermarkId = printLayout.WatermarkAssetId;

        try
        {
            if (logoId != _loadedLogoId)
            {
                var bytes = logoId is null ? null : await assets.DownloadAsync(logoId.Value);
                if (generation != Volatile.Read(ref _generation)) return;
                _loadedLogoId = logoId;
                ServerLogoBytes = bytes;
                enqueueRefresh();
            }

            if (signatureId != _loadedSignatureId)
            {
                var bytes = signatureId is null ? null : await assets.DownloadAsync(signatureId.Value);
                if (generation != Volatile.Read(ref _generation)) return;
                _loadedSignatureId = signatureId;
                ServerSignatureBytes = bytes;
                enqueueRefresh();
            }

            if (watermarkId != _loadedWatermarkId)
            {
                var bytes = watermarkId is null ? null : await assets.DownloadAsync(watermarkId.Value);
                if (generation != Volatile.Read(ref _generation)) return;
                _loadedWatermarkId = watermarkId;
                ServerWatermarkBytes = bytes;
                enqueueRefresh();
            }
        }
        catch
        {
            // Asset download is best-effort; preview falls back to whatever bytes we
            // already have. Failures surface in the status line on the next render.
        }
    }
}
