namespace ShowroomBilling.Contracts.Numbering;

/// <summary>
/// Canonical formatter for invoice numbers. Shared by Infrastructure
/// (reservation / change-number) and Desktop (change-number preview) so a
/// hand-typed core value formats identically on both sides.
/// </summary>
public static class InvoiceNumberFormatter
{
    public const int DefaultPadding = 4;

    public static string Format(string? prefix, string? suffix, long value, string fiscalYear, int padding = DefaultPadding)
    {
        var width = padding < 1 ? DefaultPadding : padding;
        var core = value.ToString($"D{width}");
        var prefixPart = string.IsNullOrWhiteSpace(prefix) ? string.Empty : prefix;
        var suffixPart = string.IsNullOrWhiteSpace(suffix) ? string.Empty : suffix;
        if (ContainsFiscalYear(prefixPart, fiscalYear) || ContainsFiscalYear(suffixPart, fiscalYear))
        {
            return $"{prefixPart}{core}{suffixPart}";
        }

        return $"{prefixPart}{fiscalYear}/{core}{suffixPart}";
    }

    /// <summary>
    /// Parses the trailing run of decimal digits from <paramref name="invoiceNumber"/>
    /// into the bill's sequence core. Returns <c>null</c> for inputs with no trailing
    /// digits (legacy custom strings like <c>LEGACY-X</c>). Format-tolerant: both
    /// <c>DDAJR/26-27/49</c> and <c>DDAJR/26-27/0049</c> parse to <c>49</c>, so a
    /// scope with mixed historical formatting still has a single canonical core
    /// per bill. Used by the reservation forward-skip and the post-delete sequence
    /// rollback to test occupancy without round-tripping through <see cref="Format"/>.
    /// </summary>
    public static long? TryParseTrailingCore(string? invoiceNumber)
    {
        if (string.IsNullOrWhiteSpace(invoiceNumber))
        {
            return null;
        }

        var end = invoiceNumber.Length;
        var start = end;
        while (start > 0 && char.IsAsciiDigit(invoiceNumber[start - 1]))
        {
            start--;
        }
        if (start == end)
        {
            return null;
        }

        return long.TryParse(invoiceNumber.AsSpan(start, end - start), out var v) ? v : null;
    }

    /// <summary>
    /// Returns the Indian fiscal-year label (e.g. <c>2026-27</c>) for the given instant, read on the IST calendar:
    /// the fiscal year turns over at 00:00 IST on 1 April, not 00:00 UTC.
    /// </summary>
    public static string ComputeFiscalYear(DateTimeOffset instant) =>
        ComputeFiscalYear(IndiaTime.DateOf(instant));

    /// <summary>Returns the Indian fiscal-year label (e.g. <c>2026-27</c>) a bill dated <paramref name="billDate"/> belongs to.</summary>
    public static string ComputeFiscalYear(DateOnly billDate)
    {
        var startYear = billDate.Month >= 4 ? billDate.Year : billDate.Year - 1;
        var endYear = startYear + 1;
        return $"{startYear}-{endYear % 100:D2}";
    }

    private static bool ContainsFiscalYear(string value, string fiscalYear)
    {
        // Format passes either an empty string or a part with visible text.
        if (value.Length == 0)
        {
            return false;
        }

        if (value.Contains(fiscalYear, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var parts = fiscalYear.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || parts[0].Length < 2)
        {
            return false;
        }

        var shortStart = parts[0][^2..];
        var shortYear = $"{shortStart}-{parts[1]}";
        return value.Contains(shortYear, StringComparison.OrdinalIgnoreCase);
    }
}
