using ShowroomBilling.Contracts.Numbering;

namespace ShowroomBilling.Application.Numbering;

public interface INumberingService
{
    public const string DocumentTypeSalesInvoice = "sales_invoice";

    Task<NumberingPreviewResponse> GetPreviewAsync(
        string documentType,
        string? fiscalYear,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Hands out the next free invoice number of the fiscal year (the current
    /// one when <paramref name="fiscalYear"/> is null) and moves the sequence
    /// past it. A caller that opened a transaction must call
    /// <c>SaveChangesAsync</c> afterwards: the sequence is saved with its rows.
    /// </summary>
    Task<ReservedNumber> ReserveAsync(
        string documentType,
        string? fiscalYear,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the invoice prefix, suffix, and zero-padding width from current cloud
    /// settings, so callers outside the numbering module (e.g. <c>ChangeInvoiceNumberAsync</c>)
    /// can format a user-typed core value identically to a fresh reservation.
    /// </summary>
    Task<(string? Prefix, string? Suffix, int Padding)> GetEffectivePrefixSuffixAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>An invoice number taken from a fiscal year's sequence.</summary>
public sealed record ReservedNumber(string FiscalYear, long ReservedValue, string FormattedNumber);
