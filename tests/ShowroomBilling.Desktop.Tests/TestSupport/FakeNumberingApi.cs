using System.Net.Http;
using ShowroomBilling.Contracts.Numbering;
using ShowroomBilling.Desktop.Services;

namespace ShowroomBilling.Desktop.Tests.TestSupport;

/// <summary>
/// The server's answer to "what number would the next bill get". A test that is
/// not about that number uses <see cref="Offline"/>, which leaves the Invoice
/// tab on its "Assigned on save" hint.
/// </summary>
internal sealed class FakeNumberingApi : INumberingApiClient
{
    public int PreviewCallCount { get; private set; }
    public Exception? Failure { get; init; }

    /// <summary>A server that cannot be reached.</summary>
    public static FakeNumberingApi Offline() => new() { Failure = new HttpRequestException(OfflineApi.Message) };

    public Task<NumberingPreviewResponse> GetPreviewAsync(
        string? documentType = null,
        string? fiscalYear = null,
        CancellationToken cancellationToken = default)
    {
        PreviewCallCount++;
        return Failure is { } failure
            ? Task.FromException<NumberingPreviewResponse>(failure)
            : Task.FromResult(new NumberingPreviewResponse(
                ShowroomId: Guid.Empty,
                FiscalYear: "26-27",
                DocumentType: documentType ?? "sales_invoice",
                PreviewValue: 1,
                FormattedNumber: "SR/26-27/0001",
                Prefix: "SR/",
                Suffix: null));
    }
}
