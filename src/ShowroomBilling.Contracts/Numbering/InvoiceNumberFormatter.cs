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
    /// <c>DEMO/26-27/49</c> and <c>DEMO/26-27/0049</c> parse to <c>49</c>, so a
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
    /// True when two numbers differ at most by zeros in front of their runs of digits, and in the
    /// case of their letters. Tally writes "SR/2026-27/051" for "SR/2026-27/51" when its voucher
    /// type fills numbers to a width, and the core need not be last ("SR/051/26-27").
    /// </summary>
    public static bool SameIgnoringPadding(string? a, string? b) =>
        a is not null && b is not null && string.Equals(PaddingFreeKey(a), PaddingFreeKey(b), StringComparison.Ordinal);

    /// <summary>
    /// The number with the zeros in front of each run of digits taken off and its letters in upper
    /// case: two numbers <see cref="SameIgnoringPadding"/> calls the same have the same key, so a
    /// list of numbers can be looked up by it.
    /// </summary>
    public static string PaddingFreeKey(string number) =>
        string.Concat(Runs(number).Select(run => IsDigits(run) ? TrimZeros(run) : run.ToUpperInvariant()));

    /// <summary>
    /// How many numbers <paramref name="to"/> comes after <paramref name="from"/> (negative when
    /// before), when the two are alike but for the value of one run of digits: "SR/0012" is one
    /// after "SR/0011", and so is "SR/12/26-27" after "SR/11/26-27". Zero when they differ only by
    /// padding; null when they differ in anything else, or in more than one run.
    /// </summary>
    public static long? StepBetween(string? from, string? to)
    {
        if (from is null || to is null)
        {
            return null;
        }

        var left = Runs(from);
        var right = Runs(to);
        if (left.Count != right.Count)
        {
            return null;
        }

        long? step = 0;
        foreach (var (l, r) in left.Zip(right))
        {
            if (SameRun(l, r))
            {
                continue;
            }

            if (step != 0 || !IsDigits(l) || !IsDigits(r)
                || !long.TryParse(l, out var lv) || !long.TryParse(r, out var rv))
            {
                return null;
            }

            step = rv - lv;
        }

        return step;
    }

    /// <summary>The text cut into runs of digits and runs of anything else.</summary>
    private static List<string> Runs(string text)
    {
        var runs = new List<string>();
        var start = 0;
        for (var i = 1; i <= text.Length; i++)
        {
            if (i == text.Length || char.IsAsciiDigit(text[i]) != char.IsAsciiDigit(text[i - 1]))
            {
                runs.Add(text[start..i]);
                start = i;
            }
        }

        return runs;
    }

    private static bool IsDigits(string run) => run.Length > 0 && char.IsAsciiDigit(run[0]);

    private static bool SameRun(string a, string b) =>
        IsDigits(a) && IsDigits(b)
            ? string.Equals(TrimZeros(a), TrimZeros(b), StringComparison.Ordinal)
            : string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string TrimZeros(string digits)
    {
        var trimmed = digits.TrimStart('0');
        return trimmed.Length == 0 ? "0" : trimmed;
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
