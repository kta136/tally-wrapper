namespace ShowroomBilling.Contracts.Numbering;

public sealed record NumberingPreviewResponse(
    Guid ShowroomId,
    string FiscalYear,
    string DocumentType,
    long PreviewValue,
    string FormattedNumber,
    string? Prefix,
    string? Suffix);
