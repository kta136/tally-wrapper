using System.Net.Http;
using System.Net.Http.Json;
using ShowroomBilling.Contracts.Numbering;

namespace ShowroomBilling.Desktop.Services;

public sealed class NumberingApiClient(HttpClient httpClient) : INumberingApiClient
{
    public async Task<NumberingPreviewResponse> GetPreviewAsync(string? documentType = null, string? fiscalYear = null, CancellationToken cancellationToken = default)
    {
        var qp = new List<string>();
        if (!string.IsNullOrWhiteSpace(documentType)) qp.Add($"documentType={Uri.EscapeDataString(documentType)}");
        if (!string.IsNullOrWhiteSpace(fiscalYear)) qp.Add($"fiscalYear={Uri.EscapeDataString(fiscalYear)}");
        var url = qp.Count == 0 ? "/api/numbering/preview" : $"/api/numbering/preview?{string.Join('&', qp)}";
        var body = await httpClient.GetFromJsonAsync<NumberingPreviewResponse>(url, cancellationToken);
        return body ?? throw new InvalidOperationException("Numbering API returned an empty preview payload.");
    }

    public async Task<ReserveNumberResponse> ReserveAsync(ReserveNumberRequest request, CancellationToken cancellationToken = default)
    {
        var response = await httpClient.PostAsJsonAsync("/api/numbering/reserve", request, cancellationToken);
        return await ApiResponseReader.ReadOrThrowAsync<ReserveNumberResponse>(response, cancellationToken);
    }
}
