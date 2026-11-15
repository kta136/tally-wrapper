namespace ShowroomBilling.Contracts.Numbering;

public sealed record NumberingPreviewResponse(
    string FiscalYear,
    long PreviewValue,
    string FormattedNumber,
    string? Prefix,
    string? Suffix);
