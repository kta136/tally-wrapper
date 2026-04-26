using ShowroomBilling.Contracts.PrintAssets;

namespace ShowroomBilling.Desktop.Services;

public interface IPrintAssetApiClient
{
    Task<PrintAssetListResponse> ListAsync(CancellationToken cancellationToken = default);

    Task<PrintAssetResponse> UploadAsync(PrintAssetUploadRequest request, CancellationToken cancellationToken = default);

    Task<byte[]?> DownloadAsync(Guid id, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
