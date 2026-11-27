using ShowroomBilling.Infrastructure.Persistence.Entities;

namespace ShowroomBilling.Infrastructure.Persistence;

internal static class CloudSettingsDefaults
{
    // Placeholders a real showroom replaces; "setup required" is judged on these.
    public const string ActiveCompanyName = "Development Company";
    public const string PrintCompanyName = "Tally Wrapper";
    public const string InvoicePrefix = "DEV-";

    /// <summary>The print company name seeded by earlier builds.</summary>
    public const string LegacyPrintCompanyName = "Showroom Billing V2";

    public static CloudSettingsEntity Create()
    {
        return new CloudSettingsEntity
        {
            Id = CloudSettingsEntity.FirstSettingsId,
            Host = "127.0.0.1",
            Port = 9000,
            TimeoutSeconds = 30,
            ActiveCompanyName = ActiveCompanyName,
            PrintCompanyName = PrintCompanyName,
            CompanyCountry = "India",
            InvoicePrefix = InvoicePrefix,
            InvoiceSuffix = string.Empty,
            InvoicePadding = 4,
            CopyOriginalDefault = true,
            CopyDuplicateDefault = false,
            CopyTriplicateDefault = false,
            PrintFontSize = 11,
            PrintTermsFontSize = 9,
            SalesLedger = "Sales",
            CashLedger = "Cash",
            CreditDebitLedger = "Card / UPI",
            CgstLedger = "CGST",
            SgstLedger = "SGST",
            RoundOffLedger = "Round Off",
            DiscountLedger = "Discount",
            // None until one is chosen: a guessed name that Tally does not have would only fail
            // in Tally, at the first push of a bill with bank charges.
            BankChargesLedger = string.Empty,
            SalesVoucherType = "Sales",
            ItemMasterDataJson = "[]",
            KaratMappingDataJson = "[]",
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
    }
}
