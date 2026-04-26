namespace ShowroomBilling.Contracts.Settings;

public sealed record ConnectionSettingsDto(
    string Host,
    int Port,
    int TimeoutSeconds,
    string ActiveCompanyName);

public sealed record NumberingSettingsDto(
    string InvoicePrefix,
    string InvoiceSuffix,
    int InvoicePadding);

public sealed record PrintSettingsDto(
    string CompanyName,
    string? CompanyGstin,
    string? CompanyPhone,
    string? CompanyAddress,
    string? CompanyState,
    string? CompanyCountry,
    string? BankName,
    string? BankAccount,
    string? BankIfsc,
    string? BankUpi,
    string? TermsAndConditions,
    bool CopyOriginalDefault,
    bool CopyDuplicateDefault,
    bool CopyTriplicateDefault,
    int PrintFontSize,
    int PrintTermsFontSize);

public sealed record LedgerMappingsDto(
    string SalesLedger,
    string CashLedger,
    string CreditDebitLedger,
    string CgstLedger,
    string SgstLedger,
    string RoundOffLedger,
    string DiscountLedger,
    string SalesVoucherType);

public sealed record MasterDataSettingsDto(
    string ItemMasterDataJson,
    string KaratMappingDataJson);

public sealed record EffectiveCloudSettingsDto(
    ConnectionSettingsDto Connection,
    NumberingSettingsDto Numbering,
    PrintSettingsDto Print,
    LedgerMappingsDto Ledgers,
    MasterDataSettingsDto Masters);

public sealed record EffectiveSettingsResponse(
    string SettingsSource,
    string Summary,
    EffectiveCloudSettingsDto Settings,
    IReadOnlyList<string> CloudOwnedCategories,
    IReadOnlyList<string> LocalOnlyCategories,
    DateTimeOffset UpdatedAtUtc);
