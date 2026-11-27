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

/// <param name="BankChargesLedger">
/// The ledger a bill's bank charges are credited to; empty until one is chosen, and needed only by a
/// bill that has bank charges. In a save, null means the sender does not know the field (a counter
/// older than it), so the server keeps what it holds; a string, empty included, is what to keep.
/// </param>
public sealed record LedgerMappingsDto(
    string SalesLedger,
    string CashLedger,
    string CreditDebitLedger,
    string CgstLedger,
    string SgstLedger,
    string RoundOffLedger,
    string DiscountLedger,
    string SalesVoucherType,
    string? BankChargesLedger = null);

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
    DateTimeOffset UpdatedAtUtc,
    bool RequiresInitialSetup = false);
