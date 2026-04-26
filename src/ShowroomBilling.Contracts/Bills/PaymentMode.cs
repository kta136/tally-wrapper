namespace ShowroomBilling.Contracts.Bills;

/// <summary>
/// Canonical payment-mode strings and case-insensitive normalization. Mirrors V1
/// <c>config.runtime.normalize_payment_mode</c>: any non-cash variant
/// (Card / UPI / Bank Transfer / Cheque / DD / Net Banking / blank / unknown)
/// collapses into the single non-cash bucket. Used by the Tally voucher builder
/// to resolve a payment mode into the configured Tally ledger name, by the
/// synthetic batch planner, and by the Invoice view's payment dropdown.
/// </summary>
public static class PaymentMode
{
    public const string Cash = "Cash";
    public const string CreditDebit = "Credit and debit";

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return CreditDebit;
        return string.Equals(value.Trim(), Cash, StringComparison.OrdinalIgnoreCase)
            ? Cash
            : CreditDebit;
    }

    public static bool IsCash(string? value) => Normalize(value) == Cash;
}
