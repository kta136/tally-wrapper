using System.Net.Http;
using ShowroomBilling.Contracts.Numbering;

namespace ShowroomBilling.Desktop.Services;

public sealed class NumberingApiClient(HttpClient httpClient) : INumberingApiClient
{
    public Task<NumberingPreviewResponse> GetPreviewAsync(string? fiscalYear = null, CancellationToken cancellationToken = default)
    {
        var url = string.IsNullOrWhiteSpace(fiscalYear)
            ? "/api/numbering/preview"
            : $"/api/numbering/preview?fiscalYear={Uri.EscapeDataString(fiscalYear)}";
        return httpClient.GetApiAsync<NumberingPreviewResponse>(url, cancellationToken);
    }
}
