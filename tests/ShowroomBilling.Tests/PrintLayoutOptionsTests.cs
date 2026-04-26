using ShowroomBilling.Printing;

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
}
