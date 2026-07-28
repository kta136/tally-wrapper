using ShowroomBilling.Printing;
using ShowroomBilling.Contracts.Settings;

namespace ShowroomBilling.Tests;

public class PrintLayoutOptionsTests
{
    [Fact]
    public void Default_uses_V1_values()
    {
        var options = PrintLayoutOptions.Default;

        Assert.Equal(10f, options.MarginLeftMm);
        Assert.Equal(10f, options.MarginTopMm);
        Assert.Equal(10f, options.MarginRightMm);
        Assert.Equal(12f, options.MarginBottomMm);
        Assert.Equal(11, options.InvoiceFontSize);
        Assert.Equal(9, options.TermsFontSize);
        Assert.Equal(PrintPageDensity.Standard, options.PageDensity);
        Assert.Equal(1f, options.InvoiceBorderThicknessPt);
        Assert.Equal(PrintLayoutSectionKeys.GstBreakup, options.BottomPinnedFromSectionKey);
    }

    [Fact]
    public void Clamped_clamps_margins_to_0_to_25()
    {
        var clamped = new PrintLayoutOptions(
            MarginLeftMm: -5f,
            MarginTopMm: 99f,
            MarginRightMm: 0f,
            MarginBottomMm: 25.1f).Clamped();

        Assert.Equal(0f, clamped.MarginLeftMm);
        Assert.Equal(25f, clamped.MarginTopMm);
        Assert.Equal(0f, clamped.MarginRightMm);
        Assert.Equal(25f, clamped.MarginBottomMm);
    }

    [Fact]
    public void Clamped_clamps_fonts_to_V1_ranges()
    {
        var small = new PrintLayoutOptions(InvoiceFontSize: 1, TermsFontSize: 1).Clamped();
        Assert.Equal(PrintLayoutLimits.InvoiceFontMin, small.InvoiceFontSize);
        Assert.Equal(PrintLayoutLimits.TermsFontMin, small.TermsFontSize);

        var huge = new PrintLayoutOptions(InvoiceFontSize: 999, TermsFontSize: 999).Clamped();
        Assert.Equal(PrintLayoutLimits.InvoiceFontMax, huge.InvoiceFontSize);
        Assert.Equal(PrintLayoutLimits.TermsFontMax, huge.TermsFontSize);
    }

    [Fact]
    public void Clamped_keeps_logo_size_within_slot()
    {
        var clamped = new PrintLayoutOptions(
            LogoWidthMm: 999f,
            LogoHeightMm: 999f).Clamped();

        Assert.InRange(clamped.LogoWidthMm, PrintLayoutLimits.LogoMinWidthMm, PrintLayoutLimits.LogoSlotWidthMm);
        Assert.InRange(clamped.LogoHeightMm, PrintLayoutLimits.LogoMinHeightMm, PrintLayoutLimits.LogoSlotHeightMm);
    }

    [Fact]
    public void Clamped_keeps_logo_offset_within_remaining_slot()
    {
        // Request a logo that fills nearly the whole slot + an oversized offset.
        var clamped = new PrintLayoutOptions(
            LogoWidthMm: PrintLayoutLimits.LogoSlotWidthMm,
            LogoHeightMm: PrintLayoutLimits.LogoSlotHeightMm,
            LogoOffsetXMm: 999f,
            LogoOffsetYMm: 999f).Clamped();

        // Size fills the slot → offset must collapse to 0.
        Assert.Equal(0f, clamped.LogoOffsetXMm);
        Assert.Equal(0f, clamped.LogoOffsetYMm);
    }

    [Fact]
    public void Clamped_keeps_signature_within_slot()
    {
        var clamped = new PrintLayoutOptions(
            SignatureWidthMm: -5f,
            SignatureHeightMm: 999f,
            SignatureOffsetXMm: -2f,
            SignatureOffsetYMm: 999f).Clamped();

        Assert.Equal(PrintLayoutLimits.SignatureMinWidthMm, clamped.SignatureWidthMm);
        Assert.Equal(PrintLayoutLimits.SignatureSlotHeightMm, clamped.SignatureHeightMm);
        Assert.Equal(0f, clamped.SignatureOffsetXMm);
        Assert.Equal(0f, clamped.SignatureOffsetYMm);
    }

    [Fact]
    public void Signature_line_is_slightly_wider_than_image_and_capped_to_slot()
    {
        var defaultLineWidth = PrintLayoutLimits.GetSignatureLineWidthMm(
            PrintLayoutLimits.DefaultSignatureWidthMm);
        var maximumLineWidth = PrintLayoutLimits.GetSignatureLineWidthMm(
            PrintLayoutLimits.SignatureSlotWidthMm);

        Assert.Equal(
            PrintLayoutLimits.DefaultSignatureWidthMm + PrintLayoutLimits.SignatureLineOverhangMm,
            defaultLineWidth);
        Assert.Equal(PrintLayoutLimits.SignatureSlotWidthMm, maximumLineWidth);
    }

    [Fact]
    public void Clamped_normalizes_watermark_page_geometry_opacity_density_and_border()
    {
        var clamped = new PrintLayoutOptions(
            WatermarkWidthMm: 500,
            WatermarkHeightMm: 500,
            WatermarkOffsetXMm: 500,
            WatermarkOffsetYMm: 500,
            WatermarkOpacity: 3,
            PageDensity: "dense",
            InvoiceBorderThicknessPt: 12).Clamped();

        Assert.Equal(PrintLayoutLimits.A4WidthMm, clamped.WatermarkWidthMm);
        Assert.Equal(PrintLayoutLimits.A4HeightMm, clamped.WatermarkHeightMm);
        Assert.Equal(0f, clamped.WatermarkOffsetXMm);
        Assert.Equal(0f, clamped.WatermarkOffsetYMm);
        Assert.Equal(1f, clamped.WatermarkOpacity);
        Assert.Equal(PrintPageDensity.Standard, clamped.PageDensity);
        Assert.Equal(4f, clamped.InvoiceBorderThicknessPt);
    }

    [Fact]
    public void Clamped_preserves_known_order_and_repairs_section_visibility_and_membership()
    {
        var clamped = new PrintLayoutOptions(
            Sections:
            [
                new(PrintLayoutSectionKeys.Terms, false, -2, 99),
                new(PrintLayoutSectionKeys.ItemsTable, false, 3, 4),
                new(PrintLayoutSectionKeys.Terms, true, 1, 1),
                new("unknown", true, 1, 1),
            ]).Clamped();

        Assert.Equal(PrintLayoutSectionKeys.Terms, clamped.Sections![0].SectionKey);
        Assert.False(clamped.Sections[0].IsVisible);
        Assert.Equal(0f, clamped.Sections[0].SpacingBeforeMm);
        Assert.Equal(20f, clamped.Sections[0].SpacingAfterMm);
        Assert.Equal(PrintLayoutSectionKeys.ItemsTable, clamped.Sections[1].SectionKey);
        Assert.True(clamped.Sections[1].IsVisible);
        Assert.Equal(PrintLayoutSectionKeys.All.Count, clamped.Sections.Count);
        Assert.Equal(PrintLayoutSectionKeys.All.Count, clamped.Sections.Select(row => row.SectionKey).Distinct().Count());
    }

    [Theory]
    [InlineData(PrintPageDensity.Compact, 0.75f)]
    [InlineData(PrintPageDensity.Standard, 1f)]
    [InlineData(PrintPageDensity.Comfortable, 1.25f)]
    public void DensityScale_matches_contract(string density, float expected)
    {
        var options = new PrintLayoutOptions(PageDensity: density).Clamped();

        Assert.Equal(expected, options.DensityScale);
    }
}
