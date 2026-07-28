using System.Text.Json;
using ShowroomBilling.Contracts.Settings;

namespace ShowroomBilling.Application.Settings;

public static class CloudSettingsValidator
{
    private const int MaxMasterJsonLength = 2 * 1024 * 1024;

    public static void Validate(UpdateEffectiveSettingsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var settings = request.Settings ?? throw new ArgumentException("Settings are required.", nameof(request));
        var connection = settings.Connection ?? throw new ArgumentException("Connection settings are required.", nameof(request));
        var numbering = settings.Numbering ?? throw new ArgumentException("Numbering settings are required.", nameof(request));
        var print = settings.Print ?? throw new ArgumentException("Print settings are required.", nameof(request));
        var ledgers = settings.Ledgers ?? throw new ArgumentException("Ledger settings are required.", nameof(request));
        var masters = settings.Masters ?? throw new ArgumentException("Master-data settings are required.", nameof(request));

        Require(connection.Host, "Connection.Host", 256);
        Require(connection.ActiveCompanyName, "Connection.ActiveCompanyName", 256);
        if (connection.Port is < 1 or > 65_535) throw new ArgumentException("Connection.Port must be between 1 and 65535.", nameof(request));
        if (connection.TimeoutSeconds is < 1 or > 300) throw new ArgumentException("Connection.TimeoutSeconds must be between 1 and 300.", nameof(request));

        Max(numbering.InvoicePrefix, "Numbering.InvoicePrefix", 64);
        Max(numbering.InvoiceSuffix, "Numbering.InvoiceSuffix", 64);
        if (numbering.InvoicePadding is < 1 or > 10) throw new ArgumentException("Numbering.InvoicePadding must be between 1 and 10.", nameof(request));

        Require(print.CompanyName, "Print.CompanyName", 256);
        Max(print.CompanyGstin, "Print.CompanyGstin", 32);
        Max(print.CompanyPhone, "Print.CompanyPhone", 64);
        Max(print.CompanyAddress, "Print.CompanyAddress", 2_000);
        Max(print.CompanyState, "Print.CompanyState", 128);
        Max(print.CompanyCountry, "Print.CompanyCountry", 128);
        Max(print.BankName, "Print.BankName", 256);
        Max(print.BankAccount, "Print.BankAccount", 128);
        Max(print.BankIfsc, "Print.BankIfsc", 32);
        Max(print.BankUpi, "Print.BankUpi", 256);
        Max(print.TermsAndConditions, "Print.TermsAndConditions", 10_000);
        if (print.PrintFontSize is < 6 or > 24) throw new ArgumentException("Print.PrintFontSize must be between 6 and 24.", nameof(request));
        if (print.PrintTermsFontSize is < 6 or > 24) throw new ArgumentException("Print.PrintTermsFontSize must be between 6 and 24.", nameof(request));

        Require(ledgers.SalesLedger, "Ledgers.SalesLedger", 256);
        Require(ledgers.CashLedger, "Ledgers.CashLedger", 256);
        Require(ledgers.CreditDebitLedger, "Ledgers.CreditDebitLedger", 256);
        Require(ledgers.CgstLedger, "Ledgers.CgstLedger", 256);
        Require(ledgers.SgstLedger, "Ledgers.SgstLedger", 256);
        Require(ledgers.RoundOffLedger, "Ledgers.RoundOffLedger", 256);
        Require(ledgers.DiscountLedger, "Ledgers.DiscountLedger", 256);
        Require(ledgers.SalesVoucherType, "Ledgers.SalesVoucherType", 256);

        ValidateJsonArray(masters.ItemMasterDataJson, "Masters.ItemMasterDataJson");
        ValidateJsonArray(masters.KaratMappingDataJson, "Masters.KaratMappingDataJson");
    }

    public static void ValidateCompanyName(string companyName)
    {
        Require(companyName, "CompanyName", 256);
    }

    public static void ValidatePrintLayout(UpdatePrintLayoutRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var layout = request.Layout ?? throw new ArgumentException("Layout is required.", nameof(request));
        ValidateRange(layout.LeftMarginCm, "LeftMarginCm", 0, 5);
        ValidateRange(layout.RightMarginCm, "RightMarginCm", 0, 5);
        ValidateRange(layout.TopMarginCm, "TopMarginCm", 0, 5);
        ValidateRange(layout.BottomMarginCm, "BottomMarginCm", 0, 5);
        if (layout.LeftMarginCm + layout.RightMarginCm >= 20)
        {
            throw new ArgumentException("Combined horizontal margins leave no printable page width.", nameof(request));
        }
        ValidatePlacement(layout.Logo, "Logo");
        ValidatePlacement(layout.Signature, "Signature");
        ValidateWatermark(layout.Watermark);
        ValidatePageLayout(layout.PageLayout);
    }

    private static void ValidatePlacement(PrintLayoutAssetPlacement? placement, string name)
    {
        if (placement is null) return;
        ValidateRange(placement.OffsetXCm, $"{name}.OffsetXCm", -20, 20);
        ValidateRange(placement.OffsetYCm, $"{name}.OffsetYCm", -30, 30);
        ValidateRange(placement.WidthCm, $"{name}.WidthCm", 0.1, 20);
        ValidateRange(placement.HeightCm, $"{name}.HeightCm", 0.1, 30);
    }

    private static void ValidateWatermark(PrintLayoutWatermarkPlacement? watermark)
    {
        if (watermark is null) return;
        if (watermark.AssetId == Guid.Empty)
        {
            throw new ArgumentException("Watermark.AssetId is required.");
        }

        ValidateRange(watermark.OffsetXCm, "Watermark.OffsetXCm", 0, PrintLayoutDefaults.A4WidthCm);
        ValidateRange(watermark.OffsetYCm, "Watermark.OffsetYCm", 0, PrintLayoutDefaults.A4HeightCm);
        ValidateRange(watermark.WidthCm, "Watermark.WidthCm", 0.1, PrintLayoutDefaults.A4WidthCm);
        ValidateRange(watermark.HeightCm, "Watermark.HeightCm", 0.1, PrintLayoutDefaults.A4HeightCm);
        ValidateRange(watermark.OpacityPercent, "Watermark.OpacityPercent", 0, 100);

        if (watermark.OffsetXCm + watermark.WidthCm > PrintLayoutDefaults.A4WidthCm)
        {
            throw new ArgumentException("Watermark horizontal position and width must fit within the A4 page.");
        }
        if (watermark.OffsetYCm + watermark.HeightCm > PrintLayoutDefaults.A4HeightCm)
        {
            throw new ArgumentException("Watermark vertical position and height must fit within the A4 page.");
        }
    }

    private static void ValidatePageLayout(PrintPageLayoutSettings? pageLayout)
    {
        if (pageLayout is null) return;

        if (!PrintPageDensity.All.Contains(pageLayout.Density, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"PageLayout.Density must be one of: {string.Join(", ", PrintPageDensity.All)}.");
        }
        ValidateRange(pageLayout.InvoiceBorderThicknessPt, "PageLayout.InvoiceBorderThicknessPt", 0, 4);

        if (pageLayout.Sections is null)
        {
            throw new ArgumentException("PageLayout.Sections is required.");
        }

        var suppliedKeys = pageLayout.Sections.Select(section => section.SectionKey).ToArray();
        if (suppliedKeys.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("PageLayout section keys are required.");
        }

        var unknownKeys = suppliedKeys
            .Where(key => !PrintLayoutSectionKeys.All.Contains(key, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (unknownKeys.Length > 0)
        {
            throw new ArgumentException($"Unknown PageLayout section key(s): {string.Join(", ", unknownKeys)}.");
        }

        var duplicateKeys = suppliedKeys
            .GroupBy(key => key, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateKeys.Length > 0)
        {
            throw new ArgumentException($"Duplicate PageLayout section key(s): {string.Join(", ", duplicateKeys)}.");
        }

        var missingKeys = PrintLayoutSectionKeys.All
            .Where(key => !suppliedKeys.Contains(key, StringComparer.Ordinal))
            .ToArray();
        if (missingKeys.Length > 0)
        {
            throw new ArgumentException($"Missing PageLayout section key(s): {string.Join(", ", missingKeys)}.");
        }

        foreach (var section in pageLayout.Sections)
        {
            if (PrintLayoutSectionKeys.Mandatory.Contains(section.SectionKey) && !section.IsVisible)
            {
                throw new ArgumentException($"PageLayout section '{section.SectionKey}' is mandatory and cannot be hidden.");
            }
            ValidateRange(section.SpacingBeforeMm, $"PageLayout.{section.SectionKey}.SpacingBeforeMm", 0, 20);
            ValidateRange(section.SpacingAfterMm, $"PageLayout.{section.SectionKey}.SpacingAfterMm", 0, 20);
        }

        if (pageLayout.BottomPinnedFromSectionKey is { } pinnedKey
            && !PrintLayoutSectionKeys.All.Contains(pinnedKey, StringComparer.Ordinal))
        {
            throw new ArgumentException($"PageLayout.BottomPinnedFromSectionKey '{pinnedKey}' is not a known section.");
        }
    }

    private static void ValidateRange(double value, string field, double min, double max)
    {
        if (!double.IsFinite(value) || value < min || value > max)
        {
            throw new ArgumentException($"{field} must be between {min} and {max}.");
        }
    }

    private static void Require(string? value, string field, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{field} is required.");
        Max(value, field, maxLength);
    }

    private static void Max(string? value, string field, int maxLength)
    {
        if (value?.Length > maxLength) throw new ArgumentException($"{field} cannot exceed {maxLength} characters.");
    }

    private static void ValidateJsonArray(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (value.Length > MaxMasterJsonLength) throw new ArgumentException($"{field} cannot exceed {MaxMasterJsonLength} characters.");
        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new ArgumentException($"{field} must contain a JSON array.");
            }
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"{field} must contain valid JSON.", ex);
        }
    }
}
