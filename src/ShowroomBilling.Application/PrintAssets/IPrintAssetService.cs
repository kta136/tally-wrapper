using ShowroomBilling.Contracts.PrintAssets;

namespace ShowroomBilling.Application.PrintAssets;

public interface IPrintAssetService
{
    Task<PrintAssetListResponse> ListAsync(CancellationToken cancellationToken = default);

    Task<PrintAssetResponse> UploadAsync(PrintAssetUploadRequest request, CancellationToken cancellationToken = default);

    Task<(PrintAssetResponse Metadata, byte[] Bytes)?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
