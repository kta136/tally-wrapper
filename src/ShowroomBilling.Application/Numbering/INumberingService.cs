using ShowroomBilling.Contracts.Numbering;

namespace ShowroomBilling.Application.Numbering;

public interface INumberingService
{
    public const string DocumentTypeSalesInvoice = "sales_invoice";


    Task<NumberingPreviewResponse> GetPreviewAsync(
        string documentType,
        string? fiscalYear,
        CancellationToken cancellationToken = default);

    Task<ReserveNumberResponse> ReserveAsync(
        ReserveNumberRequest request,
        CancellationToken cancellationToken = default);

    Task<NumberingScopesResponse> GetScopesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the invoice prefix, suffix, and zero-padding width from current cloud
    /// settings, so callers outside the numbering module (e.g. <c>ChangeInvoiceNumberAsync</c>)
    /// can format a user-typed core value identically to a fresh reservation.
    /// </summary>
    Task<(string? Prefix, string? Suffix, int Padding)> GetEffectivePrefixSuffixAsync(
        CancellationToken cancellationToken = default);
}
