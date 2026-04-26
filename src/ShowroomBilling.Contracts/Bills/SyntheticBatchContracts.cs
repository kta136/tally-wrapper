namespace ShowroomBilling.Contracts.Bills;

/// <summary>
/// V1-ported invariants for the synthetic batch planner. Do not relax without
/// confirming the business rule — the ₹1,99,000 cap is GST-compliance motivated.
/// </summary>
public static class SyntheticBatchPlanLimits
{
    public const int SoftMinBillTotal = 25_000;
    public const int HardMaxBillAmount = 199_000;
    public const int MinItemTarget = 500;
    public const decimal MinQty = 0.001m;
    public const string CashPartyName = "Cash";
    public const string NonCashPartyName = "Credit and Debit";
    public const string DefaultNarration = "Cash";
    public const int MaxItemsPerBillCap = 25;
    public const int LineCountRetryAttempts = 8;
    public const double QtyFractionMin = 0.35;
    public const double QtyFractionMax = 0.9;
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

public sealed record SyntheticBatchCreatedBill(
    Guid Id,
    string? InvoiceNumber,
    decimal GrandTotal,
    DateTimeOffset ScheduledAtUtc);

public sealed record SyntheticBatchResponse(
    int BillCount,
    decimal TotalAmount,
    IReadOnlyList<SyntheticBatchCreatedBill> CreatedBills);

public sealed record SyntheticBatchCountBoundsResponse(
    int MinBillCount,
    int MaxBillCount);
