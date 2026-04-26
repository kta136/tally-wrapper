using ShowroomBilling.Contracts.Numbering;

namespace ShowroomBilling.Infrastructure.Bills;

internal static class BillNumberChangeRules
{
    internal static bool IsDigitsOnly(string value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        foreach (var c in value)
        {
            if (c < '0' || c > '9') return false;
        }
        return true;
    }

    internal static long? ExtractTrailingDigits(string number) =>
        InvoiceNumberFormatter.TryParseTrailingCore(number);

    internal static string? BuildWarning(bool leavesGap, bool tallyDiverges, bool reservationOrphaned)
    {
        var parts = new List<string>();
        if (leavesGap) parts.Add("Chosen number is ahead of the local sequence — a gap will appear in history.");
        if (tallyDiverges) parts.Add("Tally already holds the voucher under the old number; reprint / repost from Bills to reconcile.");
        if (reservationOrphaned) parts.Add("Original reservation key stays bound to the prior number; retries won't re-allocate it.");
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }
}
