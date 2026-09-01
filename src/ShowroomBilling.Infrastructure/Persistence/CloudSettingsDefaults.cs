using ShowroomBilling.Infrastructure.Persistence.Entities;

namespace ShowroomBilling.Infrastructure.Persistence;

internal static class CloudSettingsDefaults
{
    public static CloudSettingsEntity Create()
    {
        return new CloudSettingsEntity
        {
            Id = Guid.CreateVersion7(),
            Host = "127.0.0.1",
            Port = 9000,
            TimeoutSeconds = 30,
            ActiveCompanyName = "Development Company",
            PrintCompanyName = "Tally Wrapper",
            CompanyCountry = "India",
            InvoicePrefix = "DEV-",
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
            SalesVoucherType = "Sales",
            ItemMasterDataJson = "[]",
            KaratMappingDataJson = "[]",
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
    }
}
