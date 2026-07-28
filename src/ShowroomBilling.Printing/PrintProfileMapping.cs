using ShowroomBilling.Contracts.Settings;

namespace ShowroomBilling.Printing;

/// <summary>
/// Pure mapping between the API's settings DTOs and the in-memory
/// <see cref="CompanyProfile"/> / <see cref="PrintLayoutOptions"/> types used by the
/// QuestPDF document. Lives in the Printing project so tests can reference it without
/// pulling in the WPF desktop assembly.
/// </summary>
public static class PrintProfileMapping
{
    public static CompanyProfile ToCompanyProfile(PrintSettingsDto print)
    {
        ArgumentNullException.ThrowIfNull(print);
        return new CompanyProfile(
            Name: string.IsNullOrWhiteSpace(print.CompanyName) ? "—" : print.CompanyName.Trim(),
            Address: NullIfBlank(print.CompanyAddress),
            Phone: NullIfBlank(print.CompanyPhone),
            State: NullIfBlank(print.CompanyState),
            Country: NullIfBlank(print.CompanyCountry),
            Gstin: NullIfBlank(print.CompanyGstin) ?? string.Empty,
            Huid: null,
            BankName: NullIfBlank(print.BankName),
            BankAccount: NullIfBlank(print.BankAccount),
            BankIfsc: NullIfBlank(print.BankIfsc),
            BankUpi: NullIfBlank(print.BankUpi),
            TermsAndConditions: NullIfBlank(print.TermsAndConditions));
    }

    /// <summary>
    /// Compose a single-copy preview document from the operator's live settings. Used
    /// by the settings-screen live preview; always renders one Original copy using
    /// canned sample content so changes to every print-affecting field are visible.
    /// </summary>
    public static PrintDocumentOptions ComposePreviewDocument(
        PrintSettingsDto print,
        PrintLayoutSettings layout,
        byte[]? logoBytes,
        byte[]? signatureBytes,
        byte[]? watermarkBytes = null)
    {
        ArgumentNullException.ThrowIfNull(print);
        ArgumentNullException.ThrowIfNull(layout);
        var company = ToCompanyProfile(print);
        var options = ToPrintLayoutOptions(
            layout,
            logoBytes,
            signatureBytes,
            print.PrintFontSize,
            print.PrintTermsFontSize,
            watermarkBytes);
        var content = PreviewSampleProvider.CreateSampleContent(company);
        return PrintDocumentOptions.ForInvoice(content, new[] { CopyLabel.Original }, options);
    }

    public static PrintLayoutOptions ToPrintLayoutOptions(
        PrintLayoutSettings layout,
        byte[]? logoBytes,
        byte[]? signatureBytes,
        int invoiceFontSize,
        int termsFontSize,
        byte[]? watermarkBytes = null)
    {
        ArgumentNullException.ThrowIfNull(layout);

        static float ToMm(double cm) => (float)(cm * 10.0);

        var pageLayout = layout.PageLayout ?? PrintLayoutDefaults.CreatePageLayout();
        var watermark = layout.Watermark;

        var options = new PrintLayoutOptions(
            MarginLeftMm: ToMm(layout.LeftMarginCm),
            MarginTopMm: ToMm(layout.TopMarginCm),
            MarginRightMm: ToMm(layout.RightMarginCm),
            MarginBottomMm: ToMm(layout.BottomMarginCm),
            InvoiceFontSize: invoiceFontSize,
            TermsFontSize: termsFontSize,
            LogoBytes: logoBytes,
            LogoWidthMm: layout.Logo is { WidthCm: > 0 } ? ToMm(layout.Logo.WidthCm) : PrintLayoutLimits.DefaultLogoWidthMm,
            LogoHeightMm: layout.Logo is { HeightCm: > 0 } ? ToMm(layout.Logo.HeightCm) : PrintLayoutLimits.DefaultLogoHeightMm,
            LogoOffsetXMm: layout.Logo is not null ? ToMm(layout.Logo.OffsetXCm) : PrintLayoutLimits.DefaultLogoOffsetXMm,
            LogoOffsetYMm: layout.Logo is not null ? ToMm(layout.Logo.OffsetYCm) : PrintLayoutLimits.DefaultLogoOffsetYMm,
            SignatureBytes: signatureBytes,
            SignatureWidthMm: layout.Signature is { WidthCm: > 0 } ? ToMm(layout.Signature.WidthCm) : PrintLayoutLimits.DefaultSignatureWidthMm,
            SignatureHeightMm: layout.Signature is { HeightCm: > 0 } ? ToMm(layout.Signature.HeightCm) : PrintLayoutLimits.DefaultSignatureHeightMm,
            SignatureOffsetXMm: layout.Signature is not null ? ToMm(layout.Signature.OffsetXCm) : PrintLayoutLimits.DefaultSignatureOffsetXMm,
            SignatureOffsetYMm: layout.Signature is not null ? ToMm(layout.Signature.OffsetYCm) : PrintLayoutLimits.DefaultSignatureOffsetYMm,
            WatermarkBytes: watermark is null ? null : watermarkBytes,
            WatermarkWidthMm: watermark is not null ? ToMm(watermark.WidthCm) : PrintLayoutLimits.DefaultWatermarkWidthMm,
            WatermarkHeightMm: watermark is not null ? ToMm(watermark.HeightCm) : PrintLayoutLimits.DefaultWatermarkHeightMm,
            WatermarkOffsetXMm: watermark is not null ? ToMm(watermark.OffsetXCm) : PrintLayoutLimits.DefaultWatermarkOffsetXMm,
            WatermarkOffsetYMm: watermark is not null ? ToMm(watermark.OffsetYCm) : PrintLayoutLimits.DefaultWatermarkOffsetYMm,
            WatermarkOpacity: watermark is not null ? (float)(watermark.OpacityPercent / 100.0) : PrintLayoutLimits.DefaultWatermarkOpacity,
            PageDensity: pageLayout.Density,
            InvoiceBorderThicknessPt: (float)pageLayout.InvoiceBorderThicknessPt,
            BottomPinnedFromSectionKey: pageLayout.BottomPinnedFromSectionKey,
            Sections: pageLayout.Sections
                .Select(section => new PrintSectionLayoutOptions(
                    section.SectionKey,
                    section.IsVisible,
                    (float)section.SpacingBeforeMm,
                    (float)section.SpacingAfterMm))
                .ToArray());

        return options.Clamped();
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
