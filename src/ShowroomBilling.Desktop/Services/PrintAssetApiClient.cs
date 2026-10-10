using System.Net;
using System.Net.Http;
using ShowroomBilling.Contracts.PrintAssets;

namespace ShowroomBilling.Desktop.Services;

public sealed class PrintAssetApiClient(HttpClient httpClient) : IPrintAssetApiClient
{
    public Task<PrintAssetListResponse> ListAsync(CancellationToken cancellationToken = default)
        => httpClient.GetApiAsync<PrintAssetListResponse>("/api/print-assets", cancellationToken);

    public Task<PrintAssetResponse> UploadAsync(PrintAssetUploadRequest request, CancellationToken cancellationToken = default)
        => httpClient.PostApiAsync<PrintAssetResponse>("/api/print-assets", request, cancellationToken);

    public async Task<byte[]?> DownloadAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var http = await httpClient.GetAsync($"/api/print-assets/{id}", cancellationToken);
        if (http.StatusCode is HttpStatusCode.NotFound)
        {
            return null;
        }
        await ApiResponseReader.EnsureSuccessOrThrowAsync(http, cancellationToken);
        return await http.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var http = await httpClient.DeleteAsync($"/api/print-assets/{id}", cancellationToken);
        if (http.StatusCode is HttpStatusCode.NotFound)
        {
            return false;
        }
        await ApiResponseReader.EnsureSuccessOrThrowAsync(http, cancellationToken);
        return true;
    }
}
