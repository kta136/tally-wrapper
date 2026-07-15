using System.Text.Json;

namespace ShowroomBilling.Contracts.Bills;

public sealed record BillTotalsDto(
    decimal Subtotal,
    decimal DiscountTotal,
    decimal TaxTotal,
    decimal RoundOff,
    decimal GrandTotal);

public sealed record BillLineItemDto(
    string ItemName,
    string? HsnCode,
    decimal Quantity,
    string? Unit,
    decimal Rate,
    decimal LineTotal,
    string? Karat,
    string? RawJson,
    string? StockName = null,
    decimal? GrossWeight = null,
    decimal? LessWeight = null,
    decimal? WastagePercent = null,
    decimal? LabourPerUnit = null,
    decimal? DiamondRate = null,
    decimal? Extra = null,
    string? ItemCategory = null,
    string? PricingMode = null);

public sealed record BillPayloadDto(
    string? PartyName,
    string? PartyGstin,
    string? PartyPhone,
    string? PartyAddress,
    DateOnly BillDate,
    IReadOnlyList<BillLineItemDto> Lines,
    BillTotalsDto Totals,
    string? Notes,
    string? Payment = null,
    decimal? Rate24Kt = null);

public sealed record BillRevisionResponse(
    Guid Id,
    int RevisionNo,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? SubmittedAtUtc,
    DateTimeOffset? FinalizedAtUtc,
    Guid? SupersedesRevisionId,
    BillPayloadDto Payload);

public sealed record BillResponse(
    Guid Id,
    Guid ShowroomId,
    Guid? CounterId,
    string BillType,
    string State,
    string? InvoiceNumber,
    string? FiscalYear,
    Guid? SupersededByBillId,
    Guid? CurrentRevisionId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? VoidedAtUtc,
    BillRevisionResponse? CurrentRevision,
    bool EditedAfterPush = false);

public sealed record CreateBillDraftRequest(
    Guid? CounterId,
    BillPayloadDto Payload);

public sealed record UpdateBillDraftRequest(
    BillPayloadDto Payload);

public sealed record PushBillRequest(string? Reason);

public sealed record PushPendingBillsRequest(
    string? Reason,
    int? MaxBills);

public sealed record PushSelectedBillsRequest(
    IReadOnlyList<Guid> BillIds,
    string? Reason);

public sealed record BillBatchPushItemResult(
    Guid BillId,
    bool Succeeded,
    string? BillState,
    string? InvoiceNumber,
    string? ErrorMessage);

public sealed record BillBatchPushResponse(
    int Matched,
    int Succeeded,
    int Failed,
    bool StoppedOnFailure,
    Guid? FailedBillId,
    string? FailureMessage,
    IReadOnlyList<BillBatchPushItemResult> Items);

public sealed record ReviseBillRequest(
    BillPayloadDto? InitialPayload);

public sealed record VoidBillRequest(
    string? Reason);

public sealed record RetryBillPostingRequest(
    string? Reason);

public sealed record RepostBillRequest(
    string IdempotencyKey,
    string? Reason);

public sealed record BillPostingStatusResponse(
    Guid BillId,
    string BillState,
    string? LastErrorCode,
    string? LastErrorMessage,
    string? LastRemoteId);

public sealed record BillSearchFilter(
    string? State,
    DateOnly? FromDate,
    DateOnly? ToDate,
    int? Skip,
    int? Take,
    string? Sort,
    bool? IncludeTotal = true);

public sealed record BillSummaryItem(
    Guid Id,
    string State,
    string? InvoiceNumber,
    string? PartyName,
    DateOnly? BillDate,
    decimal GrandTotal,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    bool EditedAfterPush = false);

public sealed record BillListResponse(
    int Total,
    int Skip,
    int Take,
    IReadOnlyList<BillSummaryItem> Items,
    bool? HasMore = null);

public sealed record BillBatchGetRequest(
    IReadOnlyList<Guid> BillIds);

public sealed record BillBatchGetResponse(
    IReadOnlyList<BillResponse> Bills);

public sealed record BillAuditEventDto(
    Guid Id,
    string EventType,
    string ActorType,
    string? ActorId,
    string EntityType,
    string EntityId,
    JsonElement Payload,
    DateTimeOffset CreatedAtUtc);

public sealed record BillAuditResponse(
    Guid BillId,
    IReadOnlyList<BillAuditEventDto> Events);

public sealed record ChangeBillNumberRequest(
    string NewInvoiceNumber,
    string? Reason,
    bool DryRun = false);

public sealed record ChangeBillNumberResponse(
    Guid BillId,
    string? OldInvoiceNumber,
    string NewInvoiceNumber,
    bool Committed,
    bool LeavesGap,
    bool TallyDiverges,
    bool ReservationOrphaned,
    long? SequenceNextValue,
    string? WarningSummary);

public sealed record MarkBillStateRequest(string? Reason);

public sealed record DeleteBillRequest(string? Reason, bool DryRun = false);

public sealed record DeleteBillResponse(
    Guid BillId,
    bool Committed,
    bool TallyDiverges,
    string? LastState,
    string? InvoiceNumber);

public sealed record DeleteSelectedBillsRequest(
    IReadOnlyList<Guid> BillIds,
    string? Reason);

public sealed record DeleteBillItemResult(
    Guid BillId,
    bool Deleted,
    bool TallyDiverges,
    string? Reason);

public sealed record DeleteSelectedBillsResponse(
    int Requested,
    int Deleted,
    int Skipped,
    IReadOnlyList<DeleteBillItemResult> Items);
