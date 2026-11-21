namespace ShowroomBilling.Infrastructure.Persistence.Entities;

public sealed class CloudSettingsEntity
{
    /// <summary>
    /// The id the first settings row is stored under. There is one row of settings, and the
    /// primary key is what keeps it one: of two requests that find no settings at the same
    /// moment, the second adds nothing. (A row made by a build from before 21 November 2026 has
    /// an id of its own; it is changed in place and never added to.)
    /// </summary>
    public static readonly Guid FirstSettingsId = new("00000000-0000-0000-0000-000000000001");

    public Guid Id { get; set; }

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; }

    public int TimeoutSeconds { get; set; }

    public string ActiveCompanyName { get; set; } = string.Empty;

    public string PrintCompanyName { get; set; } = string.Empty;

    public string? CompanyGstin { get; set; }

    public string? CompanyPhone { get; set; }

    public string? CompanyAddress { get; set; }

    public string? CompanyState { get; set; }

    public string? CompanyCountry { get; set; }

    public string? BankName { get; set; }

    public string? BankAccount { get; set; }

    public string? BankIfsc { get; set; }

    public string? BankUpi { get; set; }

    public string? TermsAndConditions { get; set; }

    public string InvoicePrefix { get; set; } = string.Empty;

    public string InvoiceSuffix { get; set; } = string.Empty;

    public int InvoicePadding { get; set; } = 4;

    public bool CopyOriginalDefault { get; set; }

    public bool CopyDuplicateDefault { get; set; }

    public bool CopyTriplicateDefault { get; set; }

    public int PrintFontSize { get; set; }

    public int PrintTermsFontSize { get; set; }

    public string SalesLedger { get; set; } = string.Empty;

    public string CashLedger { get; set; } = string.Empty;

    public string CreditDebitLedger { get; set; } = string.Empty;

    public string CgstLedger { get; set; } = string.Empty;

    public string SgstLedger { get; set; } = string.Empty;

    public string RoundOffLedger { get; set; } = string.Empty;

    public string DiscountLedger { get; set; } = string.Empty;

    public string SalesVoucherType { get; set; } = string.Empty;

    public string ItemMasterDataJson { get; set; } = "[]";

    public string KaratMappingDataJson { get; set; } = "[]";

    public string PrintLayoutJson { get; set; } = "{}";

    /// <summary>
    /// The shop hours a batch keeps to (<c>BatchHoursSettings</c>, as JSON). <c>{}</c>, which is
    /// also the column's default, means none are set.
    /// </summary>
    public string BatchHoursJson { get; set; } = "{}";

    public DateTimeOffset UpdatedAtUtc { get; set; }
}
