using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Settings;

namespace ShowroomBilling.Printing;

public enum CopyLabel
{
    Original,
    Duplicate,
    Triplicate,
}

public static class CopyLabelExtensions
{
    public static string ToDisplayText(this CopyLabel label) => label switch
    {
        CopyLabel.Original => "Original for Recipient",
        CopyLabel.Duplicate => "Duplicate for Transporter",
        CopyLabel.Triplicate => "Triplicate for Supplier",
        _ => label.ToString(),
    };
}

public sealed record CompanyProfile(
    string Name,
    string? Address,
    string? Phone,
    string? State,
    string? Country,
    string Gstin,
    string? Huid,
    string? BankName,
    string? BankAccount,
    string? BankIfsc,
    string? BankUpi,
    string? TermsAndConditions)
{
    public bool HasAnyBankField =>
        !string.IsNullOrWhiteSpace(BankName)
        || !string.IsNullOrWhiteSpace(BankAccount)
        || !string.IsNullOrWhiteSpace(BankIfsc)
        || !string.IsNullOrWhiteSpace(BankUpi);
}

/// <summary>
/// Layout constants ported from V1 (core/print_layout.py). All values are in millimetres
/// to keep the QuestPDF pipeline in a single unit.
/// </summary>
public static class PrintLayoutLimits
{
    // V1 slot / asset sizes expressed in px at 96 dpi → mm.
    private const float PxToMm = 25.4f / 96f;

    public const float LogoSlotWidthMm = 240f * PxToMm;   // ≈63.5
    public const float LogoSlotHeightMm = 84f * PxToMm;   // ≈22.2
    public const float SignatureSlotWidthMm = 260f * PxToMm; // ≈69mm — must fit 40% footer col (~203pt = ~271px)
    public const float SignatureSlotHeightMm = 104f * PxToMm; // ≈27.5mm — taller slot widens gap between "For …" and "Authorised Signatory" and allows larger signature image

    public const float LogoMinWidthMm = 40f * PxToMm;     // ≈10.6
    public const float LogoMinHeightMm = 24f * PxToMm;    // ≈6.4
    public const float SignatureMinWidthMm = 60f * PxToMm; // ≈15.9
    public const float SignatureMinHeightMm = 24f * PxToMm; // ≈6.4
    public const float SignatureLineOverhangMm = 4f; // 2 mm beyond the configured image on each side

    public const float DefaultLogoWidthMm = 160f * PxToMm;  // ≈42.3
    public const float DefaultLogoHeightMm = 52f * PxToMm;  // ≈13.8
    public const float DefaultLogoOffsetXMm = 40f * PxToMm; // ≈10.6
    public const float DefaultLogoOffsetYMm = 16f * PxToMm; // ≈4.2

    public const float DefaultSignatureWidthMm = 130f * PxToMm;  // ≈34.4 (reduced from 160px)
    public const float DefaultSignatureHeightMm = 80f * PxToMm;  // ≈21.2 — taller default to match enlarged slot
    public const float DefaultSignatureOffsetXMm = 30f * PxToMm; // ≈7.9
    public const float DefaultSignatureOffsetYMm = 8f * PxToMm;  // ≈2.1 (reduced from 13px)

    // Margins, fonts, page size and watermark defaults are defined once, in
    // centimetres, in Contracts (PrintLayoutDefaults); these are the same
    // numbers in the renderer's millimetres.
    private const float MmPerCm = 10f;

    public const float MarginMinMm = 0f;
    public const float MarginMaxMm = (float)PrintLayoutDefaults.MarginMaxCm * MmPerCm;

    public const float DefaultMarginLeftMm = (float)PrintLayoutDefaults.MarginCm * MmPerCm;
    public const float DefaultMarginTopMm = DefaultMarginLeftMm;
    public const float DefaultMarginRightMm = DefaultMarginLeftMm;
    // V1's value, and the only default that differs from the stored one
    // (1.0 cm). It applies only when a document is rendered with no saved
    // layout at all; the API always supplies one.
    public const float DefaultMarginBottomMm = 12f;

    public const int InvoiceFontMin = PrintLayoutDefaults.InvoiceFontMin;
    public const int InvoiceFontMax = PrintLayoutDefaults.InvoiceFontMax;
    public const int TermsFontMin = PrintLayoutDefaults.TermsFontMin;
    public const int TermsFontMax = PrintLayoutDefaults.TermsFontMax;

    public const int DefaultInvoiceFontSize = 11;
    public const int DefaultTermsFontSize = 9;

    public static float GetSignatureLineWidthMm(float signatureImageWidthMm) =>
        Math.Min(SignatureSlotWidthMm, signatureImageWidthMm + SignatureLineOverhangMm);

    public const float A4WidthMm = (float)PrintLayoutDefaults.A4WidthCm * MmPerCm;
    public const float A4HeightMm = (float)PrintLayoutDefaults.A4HeightCm * MmPerCm;
    public const float WatermarkMinSizeMm = 1f;
    public const float DefaultWatermarkWidthMm = (float)PrintLayoutDefaults.WatermarkWidthCm * MmPerCm;
    public const float DefaultWatermarkHeightMm = (float)PrintLayoutDefaults.WatermarkHeightCm * MmPerCm;
    public const float DefaultWatermarkOffsetXMm = (float)PrintLayoutDefaults.WatermarkOffsetXCm * MmPerCm;
    public const float DefaultWatermarkOffsetYMm = (float)PrintLayoutDefaults.WatermarkOffsetYCm * MmPerCm;
    public const float DefaultWatermarkOpacity = (float)PrintLayoutDefaults.WatermarkOpacityPercent / 100f;

    public const float SectionSpacingMinMm = 0f;
    public const float SectionSpacingMaxMm = 20f;
    public const float InvoiceBorderMinPt = 0f;
    public const float InvoiceBorderMaxPt = 4f;
    public const float DefaultInvoiceBorderPt = 1f;
}

public sealed record PrintSectionLayoutOptions(
    string SectionKey,
    bool IsVisible,
    float SpacingBeforeMm,
    float SpacingAfterMm);

public sealed record PrintLayoutOptions(
    float MarginLeftMm = PrintLayoutLimits.DefaultMarginLeftMm,
    float MarginTopMm = PrintLayoutLimits.DefaultMarginTopMm,
    float MarginRightMm = PrintLayoutLimits.DefaultMarginRightMm,
    float MarginBottomMm = PrintLayoutLimits.DefaultMarginBottomMm,
    int InvoiceFontSize = PrintLayoutLimits.DefaultInvoiceFontSize,
    int TermsFontSize = PrintLayoutLimits.DefaultTermsFontSize,
    byte[]? LogoBytes = null,
    float LogoWidthMm = PrintLayoutLimits.DefaultLogoWidthMm,
    float LogoHeightMm = PrintLayoutLimits.DefaultLogoHeightMm,
    float LogoOffsetXMm = PrintLayoutLimits.DefaultLogoOffsetXMm,
    float LogoOffsetYMm = PrintLayoutLimits.DefaultLogoOffsetYMm,
    byte[]? SignatureBytes = null,
    float SignatureWidthMm = PrintLayoutLimits.DefaultSignatureWidthMm,
    float SignatureHeightMm = PrintLayoutLimits.DefaultSignatureHeightMm,
    float SignatureOffsetXMm = PrintLayoutLimits.DefaultSignatureOffsetXMm,
    float SignatureOffsetYMm = PrintLayoutLimits.DefaultSignatureOffsetYMm,
    byte[]? WatermarkBytes = null,
    float WatermarkWidthMm = PrintLayoutLimits.DefaultWatermarkWidthMm,
    float WatermarkHeightMm = PrintLayoutLimits.DefaultWatermarkHeightMm,
    float WatermarkOffsetXMm = PrintLayoutLimits.DefaultWatermarkOffsetXMm,
    float WatermarkOffsetYMm = PrintLayoutLimits.DefaultWatermarkOffsetYMm,
    float WatermarkOpacity = PrintLayoutLimits.DefaultWatermarkOpacity,
    string PageDensity = PrintPageDensity.Standard,
    float InvoiceBorderThicknessPt = PrintLayoutLimits.DefaultInvoiceBorderPt,
    string? BottomPinnedFromSectionKey = PrintLayoutSectionKeys.GstBreakup,
    IReadOnlyList<PrintSectionLayoutOptions>? Sections = null)
{
    public static PrintLayoutOptions Default { get; } = new();

    /// <summary>Return a copy with all numeric fields clamped to V1 ranges.</summary>
    public PrintLayoutOptions Clamped()
    {
        var logoWidth = Math.Clamp(LogoWidthMm, PrintLayoutLimits.LogoMinWidthMm, PrintLayoutLimits.LogoSlotWidthMm);
        var logoHeight = Math.Clamp(LogoHeightMm, PrintLayoutLimits.LogoMinHeightMm, PrintLayoutLimits.LogoSlotHeightMm);
        var sigWidth = Math.Clamp(SignatureWidthMm, PrintLayoutLimits.SignatureMinWidthMm, PrintLayoutLimits.SignatureSlotWidthMm);
        var sigHeight = Math.Clamp(SignatureHeightMm, PrintLayoutLimits.SignatureMinHeightMm, PrintLayoutLimits.SignatureSlotHeightMm);
        var watermarkWidth = Math.Clamp(WatermarkWidthMm, PrintLayoutLimits.WatermarkMinSizeMm, PrintLayoutLimits.A4WidthMm);
        var watermarkHeight = Math.Clamp(WatermarkHeightMm, PrintLayoutLimits.WatermarkMinSizeMm, PrintLayoutLimits.A4HeightMm);

        var normalizedSections = new List<PrintSectionLayoutOptions>();
        var seenSections = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in Sections ?? Array.Empty<PrintSectionLayoutOptions>())
        {
            if (!PrintLayoutSectionKeys.All.Contains(section.SectionKey, StringComparer.Ordinal)
                || !seenSections.Add(section.SectionKey))
            {
                continue;
            }

            normalizedSections.Add(section with
            {
                IsVisible = PrintLayoutSectionKeys.Mandatory.Contains(section.SectionKey) || section.IsVisible,
                SpacingBeforeMm = Math.Clamp(
                    section.SpacingBeforeMm,
                    PrintLayoutLimits.SectionSpacingMinMm,
                    PrintLayoutLimits.SectionSpacingMaxMm),
                SpacingAfterMm = Math.Clamp(
                    section.SpacingAfterMm,
                    PrintLayoutLimits.SectionSpacingMinMm,
                    PrintLayoutLimits.SectionSpacingMaxMm),
            });
        }
        foreach (var missingKey in PrintLayoutSectionKeys.All.Where(key => !seenSections.Contains(key)))
        {
            normalizedSections.Add(new PrintSectionLayoutOptions(missingKey, true, 0f, 0f));
        }

        var density = PrintPageDensity.All.Contains(PageDensity, StringComparer.Ordinal)
            ? PageDensity
            : PrintPageDensity.Standard;
        var pinnedFrom = BottomPinnedFromSectionKey is not null
            && PrintLayoutSectionKeys.All.Contains(BottomPinnedFromSectionKey, StringComparer.Ordinal)
                ? BottomPinnedFromSectionKey
                : null;

        return this with
        {
            MarginLeftMm = Math.Clamp(MarginLeftMm, PrintLayoutLimits.MarginMinMm, PrintLayoutLimits.MarginMaxMm),
            MarginTopMm = Math.Clamp(MarginTopMm, PrintLayoutLimits.MarginMinMm, PrintLayoutLimits.MarginMaxMm),
            MarginRightMm = Math.Clamp(MarginRightMm, PrintLayoutLimits.MarginMinMm, PrintLayoutLimits.MarginMaxMm),
            MarginBottomMm = Math.Clamp(MarginBottomMm, PrintLayoutLimits.MarginMinMm, PrintLayoutLimits.MarginMaxMm),
            InvoiceFontSize = Math.Clamp(InvoiceFontSize, PrintLayoutLimits.InvoiceFontMin, PrintLayoutLimits.InvoiceFontMax),
            TermsFontSize = Math.Clamp(TermsFontSize, PrintLayoutLimits.TermsFontMin, PrintLayoutLimits.TermsFontMax),
            LogoWidthMm = logoWidth,
            LogoHeightMm = logoHeight,
            LogoOffsetXMm = Math.Clamp(LogoOffsetXMm, 0f, Math.Max(0f, PrintLayoutLimits.LogoSlotWidthMm - logoWidth)),
            LogoOffsetYMm = Math.Clamp(LogoOffsetYMm, 0f, Math.Max(0f, PrintLayoutLimits.LogoSlotHeightMm - logoHeight)),
            SignatureWidthMm = sigWidth,
            SignatureHeightMm = sigHeight,
            SignatureOffsetXMm = Math.Clamp(SignatureOffsetXMm, 0f, Math.Max(0f, PrintLayoutLimits.SignatureSlotWidthMm - sigWidth)),
            SignatureOffsetYMm = Math.Clamp(SignatureOffsetYMm, 0f, Math.Max(0f, PrintLayoutLimits.SignatureSlotHeightMm - sigHeight)),
            WatermarkWidthMm = watermarkWidth,
            WatermarkHeightMm = watermarkHeight,
            WatermarkOffsetXMm = Math.Clamp(WatermarkOffsetXMm, 0f, Math.Max(0f, PrintLayoutLimits.A4WidthMm - watermarkWidth)),
            WatermarkOffsetYMm = Math.Clamp(WatermarkOffsetYMm, 0f, Math.Max(0f, PrintLayoutLimits.A4HeightMm - watermarkHeight)),
            WatermarkOpacity = Math.Clamp(WatermarkOpacity, 0f, 1f),
            PageDensity = density,
            InvoiceBorderThicknessPt = Math.Clamp(
                InvoiceBorderThicknessPt,
                PrintLayoutLimits.InvoiceBorderMinPt,
                PrintLayoutLimits.InvoiceBorderMaxPt),
            BottomPinnedFromSectionKey = pinnedFrom,
            Sections = normalizedSections,
        };
    }

    public float DensityScale => PageDensity switch
    {
        PrintPageDensity.Compact => 0.75f,
        PrintPageDensity.Comfortable => 1.25f,
        _ => 1f,
    };
}

public sealed record BillPrintContent(
    string InvoiceNumber,
    DateOnly BillDate,
    string? PartyName,
    string? PartyGstin,
    string? PartyPhone,
    string? PartyAddress,
    string? Payment,
    decimal? Rate24Kt,
    IReadOnlyList<BillLineItemDto> Lines,
    BillTotalsDto Totals,
    string? Notes,
    CompanyProfile Company);

public sealed record PrintDocumentOptions(
    BillPrintContent Content,
    IReadOnlyList<CopyLabel> Copies,
    PrintLayoutOptions Layout)
{
    public static PrintDocumentOptions ForInvoice(BillPrintContent content, IReadOnlyList<CopyLabel> copies, PrintLayoutOptions? layout = null) =>
        new(content, copies.Count == 0 ? new[] { CopyLabel.Original } : copies, layout ?? PrintLayoutOptions.Default);
}
