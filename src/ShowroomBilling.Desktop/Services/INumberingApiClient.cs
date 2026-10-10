using ShowroomBilling.Contracts.Numbering;

namespace ShowroomBilling.Desktop.Services;

public interface INumberingApiClient
{
    Task<NumberingPreviewResponse> GetPreviewAsync(string? documentType = null, string? fiscalYear = null, CancellationToken cancellationToken = default);
}
