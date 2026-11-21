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

    public const int MaxItemsPerBillCap = 25;
}

/// <param name="BatchId">
/// An id the Desktop makes up for this batch, so that it can ask afterwards how far the batch got
/// (<see cref="SyntheticBatchOutcomeResponse"/>) when its request ended without an answer. A batch
/// sent without one is made all the same, and cannot be asked about.
/// </param>
/// <param name="ExpectedHours">
/// The shop hours the operator was shown and said yes to. When they are no longer the saved ones
/// (another counter changed them meanwhile) the batch is refused, so that no bill is dated by hours
/// the operator did not see. A batch sent without them is made by the saved hours.
/// </param>
public sealed record SyntheticBatchRequest(
    long TotalAmount,
    long MaxBillAmount,
    decimal Rate24Kt,
    string PaymentMode,
    int MinItemsPerBill,
    int MaxItemsPerBill,
    DateTimeOffset StartAtUtc,
    DateTimeOffset EndAtUtc,
    IReadOnlyList<string> SelectedKaratLabels,
    Guid? BatchId = null,
    BatchHoursSettings? ExpectedHours = null);

/// <summary>A batch that was made whole: how many bills, what they come to, and the first and last bill number.</summary>
public sealed record SyntheticBatchResponse(
    int BillCount,
    decimal TotalAmount,
    string? FirstInvoiceNumber = null,
    string? LastInvoiceNumber = null);

/// <summary>
/// How far a batch got, for a Desktop whose request ended without an answer (the run was cancelled,
/// the server took too long, the connection dropped) or with an error part-way. The counts are of
/// bills saved; <paramref name="Finished"/> is false while the server is still saving.
/// <paramref name="LastSaveInDoubt"/> is true when the batch stopped because saving a bill failed in
/// a way that does not say whether that bill was stored (the database stopped answering at its
/// commit): one bill more than <paramref name="BillCount"/> may then exist.
/// </summary>
public sealed record SyntheticBatchOutcomeResponse(
    bool Finished,
    int PlannedBillCount,
    int BillCount,
    decimal TotalAmount,
    string? FirstInvoiceNumber,
    string? LastInvoiceNumber,
    bool LastSaveInDoubt = false);

/// <summary>
/// When a batch may be dated from: <paramref name="EarliestUtc"/> is the first minute after the last
/// bill (a bill dated an earlier day than it was saved counts as the end of that day), or null when
/// there is no bill. A batch is never dated later than now. <paramref name="Hours"/> are the shop
/// hours a batch keeps to, so the dialog can say how many bills its times have room for; null from a
/// server built before 21 November 2026, which has none.
/// </summary>
public sealed record SyntheticBatchWindowResponse(DateTimeOffset? EarliestUtc, BatchHoursSettings? Hours = null);

/// <summary>
/// The hours a batch dates bills in (owner, 21 November 2026), set once in Settings → Admin and
/// kept with the shared settings. All in India's time.
/// </summary>
/// <param name="Opens">When the shop opens. With <paramref name="Closes"/>: a batch dates a bill only from this minute to that one. Both null: any time of day.</param>
/// <param name="Closes">When the shop closes; later than <paramref name="Opens"/>, on the same day.</param>
/// <param name="ClosedOn">The weekday the shop is closed, on which a batch dates no bill; null when it is open every day.</param>
/// <param name="GapMinutes">
/// The fewest minutes between two bills of a batch; 0 or 1 for none. It is kept when the times
/// have room for the bills that far apart; when they do not, the batch is made all the same, with
/// the bills as far apart as fits.
/// </param>
public sealed record BatchHoursSettings(
    TimeOnly? Opens = null,
    TimeOnly? Closes = null,
    DayOfWeek? ClosedOn = null,
    int GapMinutes = 0);

public static class BatchHoursLimits
{
    /// <summary>The largest gap that can be asked for between two bills, in minutes.</summary>
    public const int MaxGapMinutes = 120;
}

public sealed record BatchHoursResponse(BatchHoursSettings Hours, DateTimeOffset UpdatedAtUtc);

public sealed record UpdateBatchHoursRequest(BatchHoursSettings Hours);
