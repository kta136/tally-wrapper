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

    public const float MarginMinMm = 0f;
    public const float MarginMaxMm = 25f;

    public const float DefaultMarginLeftMm = 10f;
    public const float DefaultMarginTopMm = 10f;
    public const float DefaultMarginRightMm = 10f;
    public const float DefaultMarginBottomMm = 12f;

    public const int InvoiceFontMin = 8;
    public const int InvoiceFontMax = 18;
    public const int TermsFontMin = 7;
    public const int TermsFontMax = 16;

    public const int DefaultInvoiceFontSize = 11;
    public const int DefaultTermsFontSize = 9;

    public static float GetSignatureLineWidthMm(float signatureImageWidthMm) =>
        Math.Min(SignatureSlotWidthMm, signatureImageWidthMm + SignatureLineOverhangMm);

    public const float A4WidthMm = 210f;
    public const float A4HeightMm = 297f;
    public const float WatermarkMinSizeMm = 1f;
    public const float DefaultWatermarkWidthMm = 120f;
    public const float DefaultWatermarkHeightMm = 120f;
    public const float DefaultWatermarkOffsetXMm = 45f;
    public const float DefaultWatermarkOffsetYMm = 88.5f;
    public const float DefaultWatermarkOpacity = 0.15f;

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
        static float ClampF(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
        static int ClampI(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

        var logoWidth = ClampF(LogoWidthMm, PrintLayoutLimits.LogoMinWidthMm, PrintLayoutLimits.LogoSlotWidthMm);
        var logoHeight = ClampF(LogoHeightMm, PrintLayoutLimits.LogoMinHeightMm, PrintLayoutLimits.LogoSlotHeightMm);
        var sigWidth = ClampF(SignatureWidthMm, PrintLayoutLimits.SignatureMinWidthMm, PrintLayoutLimits.SignatureSlotWidthMm);
        var sigHeight = ClampF(SignatureHeightMm, PrintLayoutLimits.SignatureMinHeightMm, PrintLayoutLimits.SignatureSlotHeightMm);
        var watermarkWidth = ClampF(WatermarkWidthMm, PrintLayoutLimits.WatermarkMinSizeMm, PrintLayoutLimits.A4WidthMm);
        var watermarkHeight = ClampF(WatermarkHeightMm, PrintLayoutLimits.WatermarkMinSizeMm, PrintLayoutLimits.A4HeightMm);

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
                SpacingBeforeMm = ClampF(
                    section.SpacingBeforeMm,
                    PrintLayoutLimits.SectionSpacingMinMm,
                    PrintLayoutLimits.SectionSpacingMaxMm),
                SpacingAfterMm = ClampF(
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
            MarginLeftMm = ClampF(MarginLeftMm, PrintLayoutLimits.MarginMinMm, PrintLayoutLimits.MarginMaxMm),
            MarginTopMm = ClampF(MarginTopMm, PrintLayoutLimits.MarginMinMm, PrintLayoutLimits.MarginMaxMm),
            MarginRightMm = ClampF(MarginRightMm, PrintLayoutLimits.MarginMinMm, PrintLayoutLimits.MarginMaxMm),
            MarginBottomMm = ClampF(MarginBottomMm, PrintLayoutLimits.MarginMinMm, PrintLayoutLimits.MarginMaxMm),
            InvoiceFontSize = ClampI(InvoiceFontSize, PrintLayoutLimits.InvoiceFontMin, PrintLayoutLimits.InvoiceFontMax),
            TermsFontSize = ClampI(TermsFontSize, PrintLayoutLimits.TermsFontMin, PrintLayoutLimits.TermsFontMax),
            LogoWidthMm = logoWidth,
            LogoHeightMm = logoHeight,
            LogoOffsetXMm = ClampF(LogoOffsetXMm, 0f, Math.Max(0f, PrintLayoutLimits.LogoSlotWidthMm - logoWidth)),
            LogoOffsetYMm = ClampF(LogoOffsetYMm, 0f, Math.Max(0f, PrintLayoutLimits.LogoSlotHeightMm - logoHeight)),
            SignatureWidthMm = sigWidth,
            SignatureHeightMm = sigHeight,
            SignatureOffsetXMm = ClampF(SignatureOffsetXMm, 0f, Math.Max(0f, PrintLayoutLimits.SignatureSlotWidthMm - sigWidth)),
            SignatureOffsetYMm = ClampF(SignatureOffsetYMm, 0f, Math.Max(0f, PrintLayoutLimits.SignatureSlotHeightMm - sigHeight)),
            WatermarkWidthMm = watermarkWidth,
            WatermarkHeightMm = watermarkHeight,
            WatermarkOffsetXMm = ClampF(WatermarkOffsetXMm, 0f, Math.Max(0f, PrintLayoutLimits.A4WidthMm - watermarkWidth)),
            WatermarkOffsetYMm = ClampF(WatermarkOffsetYMm, 0f, Math.Max(0f, PrintLayoutLimits.A4HeightMm - watermarkHeight)),
            WatermarkOpacity = ClampF(WatermarkOpacity, 0f, 1f),
            PageDensity = density,
            InvoiceBorderThicknessPt = ClampF(
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
