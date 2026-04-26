using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using ShowroomBilling.Contracts.PrintAssets;

namespace ShowroomBilling.Desktop.Services;

public sealed class PrintAssetApiClient(HttpClient httpClient) : IPrintAssetApiClient
{
    public async Task<PrintAssetListResponse> ListAsync(CancellationToken cancellationToken = default)
    {
        var response = await httpClient.GetFromJsonAsync<PrintAssetListResponse>(
            "/api/print-assets", cancellationToken);
        return response ?? new PrintAssetListResponse(Array.Empty<PrintAssetResponse>());
    }

    public async Task<PrintAssetResponse> UploadAsync(PrintAssetUploadRequest request, CancellationToken cancellationToken = default)
    {
        var http = await httpClient.PostAsJsonAsync("/api/print-assets", request, cancellationToken);
        await ApiResponseReader.EnsureSuccessOrThrowAsync(http, cancellationToken);
        var response = await http.Content.ReadFromJsonAsync<PrintAssetResponse>(cancellationToken: cancellationToken);
        return response ?? throw new InvalidOperationException("Empty upload response.");
    }

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
