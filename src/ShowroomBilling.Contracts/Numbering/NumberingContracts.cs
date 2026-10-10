namespace ShowroomBilling.Contracts.Numbering;

public sealed record NumberingPreviewResponse(
    Guid ShowroomId,
    string FiscalYear,
    string DocumentType,
    long PreviewValue,
    string FormattedNumber,
    string? Prefix,
    string? Suffix);

public sealed record ReserveNumberRequest(
    string IdempotencyKey,
    string DocumentType,
    string? FiscalYear,
    string? ReservedForReference);

public sealed record ReserveNumberResponse(
    Guid ReservationId,
    string IdempotencyKey,
    Guid ShowroomId,
    string FiscalYear,
    string DocumentType,
    long ReservedValue,
    string FormattedNumber,
    DateTimeOffset ReservedAtUtc,
    bool AlreadyExisted);
