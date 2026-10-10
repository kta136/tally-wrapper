namespace ShowroomBilling.Infrastructure.Bills;

internal static class BillNumberChangeRules
{
    internal static bool IsDigitsOnly(string value) =>
        !string.IsNullOrEmpty(value) && value.All(char.IsAsciiDigit);

    internal static string? BuildWarning(bool leavesGap, bool tallyDiverges, bool reservationOrphaned)
    {
        var parts = new List<string>();
        if (leavesGap) parts.Add("Chosen number is ahead of the local sequence — a gap will appear in history.");
        if (tallyDiverges) parts.Add("Tally holds this voucher under the old number. The bill moves to pending; push it to update the same Tally voucher to the new number. Do not repost, which would create a second voucher.");
        if (reservationOrphaned) parts.Add("Original reservation key stays bound to the prior number; retries won't re-allocate it.");
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }
}
