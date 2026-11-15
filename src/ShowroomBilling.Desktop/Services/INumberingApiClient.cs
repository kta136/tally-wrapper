using ShowroomBilling.Contracts.Numbering;

namespace ShowroomBilling.Desktop.Services;

public interface INumberingApiClient
{
    Task<NumberingPreviewResponse> GetPreviewAsync(string? fiscalYear = null, CancellationToken cancellationToken = default);
}
