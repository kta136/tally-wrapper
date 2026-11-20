namespace ShowroomBilling.Contracts.Bills;

/// <summary>
/// Limits of the synthetic batch planner. The ₹1,99,000 cap is GST-compliance motivated (V1) and is
/// not relaxed. The smallest bill, the smallest line weight and the weight step are the owner's
/// (20 November 2026), and replace V1's ₹25,000 and 0.001 g.
/// </summary>
public static class SyntheticBatchPlanLimits
{
    /// <summary>
    /// The smallest grand total (with GST) of a synthetic bill. Also the smallest total a batch may
    /// ask for and the smallest max bill amount.
    /// </summary>
    public const int MinBillTotal = 50_000;
    public const int HardMaxBillAmount = 199_000;

    /// <summary>No line of a synthetic bill weighs less than this, in grams.</summary>
    public const decimal MinLineWeight = 1m;

    /// <summary>A line's weight is a whole number of these grams: two decimals.</summary>
    public const decimal WeightStep = 0.01m;

    /// <summary>The note every synthetic bill carries, whatever its payment mode (V1).</summary>
    public const string DefaultNarration = "Cash";
    public const int MaxItemsPerBillCap = 25;
}

public sealed record SyntheticBatchRequest(
    long TotalAmount,
    long MaxBillAmount,
    decimal Rate24Kt,
    string PaymentMode,
    int MinItemsPerBill,
    int MaxItemsPerBill,
    DateTimeOffset StartAtUtc,
    DateTimeOffset EndAtUtc,
    IReadOnlyList<string> SelectedKaratLabels);

public sealed record SyntheticBatchResponse(
    int BillCount,
    decimal TotalAmount);
