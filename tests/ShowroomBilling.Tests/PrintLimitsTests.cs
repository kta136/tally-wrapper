using ShowroomBilling.Application;
using ShowroomBilling.Application.Settings;
using ShowroomBilling.Contracts.PrintAssets;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Printing;

namespace ShowroomBilling.Tests;

/// <summary>
/// One set of print limits: a layout the API accepts is the layout that
/// prints. Before, the API took a 4 cm margin or a 22-point font and the
/// renderer quietly used 2.5 cm and 18.
/// </summary>
public sealed class PrintLimitsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1.0)]
    [InlineData(PrintLayoutDefaults.MarginMaxCm)]
    public void A_margin_the_api_accepts_is_not_changed_by_the_renderer(double marginCm)
    {
        var layout = Layout(marginCm);

        CloudSettingsValidator.ValidatePrintLayout(new UpdatePrintLayoutRequest(layout));

        var rendered = new PrintLayoutOptions(MarginLeftMm: (float)(marginCm * 10)).Clamped();
        Assert.Equal((float)(marginCm * 10), rendered.MarginLeftMm, 3);
    }

    [Theory]
    [InlineData(2.6)]
    [InlineData(4.0)]
    [InlineData(-0.1)]
    public void A_margin_the_renderer_would_change_is_refused(double marginCm)
    {
        var ex = Assert.Throws<RequestValidationException>(
            () => CloudSettingsValidator.ValidatePrintLayout(new UpdatePrintLayoutRequest(Layout(marginCm))));

        Assert.Contains("LeftMarginCm", ex.Message);
    }

    [Fact]
    public void The_renderer_reads_its_limits_from_the_shared_definition()
    {
        Assert.Equal(PrintLayoutDefaults.MarginMaxCm * 10, PrintLayoutLimits.MarginMaxMm, 3);
        Assert.Equal(PrintLayoutDefaults.InvoiceFontMin, PrintLayoutLimits.InvoiceFontMin);
        Assert.Equal(PrintLayoutDefaults.InvoiceFontMax, PrintLayoutLimits.InvoiceFontMax);
        Assert.Equal(PrintLayoutDefaults.TermsFontMin, PrintLayoutLimits.TermsFontMin);
        Assert.Equal(PrintLayoutDefaults.TermsFontMax, PrintLayoutLimits.TermsFontMax);
        Assert.Equal(210f, PrintLayoutLimits.A4WidthMm, 3);
        Assert.Equal(297f, PrintLayoutLimits.A4HeightMm, 3);
        Assert.Equal(120f, PrintLayoutLimits.DefaultWatermarkWidthMm, 3);
        Assert.Equal(88.5f, PrintLayoutLimits.DefaultWatermarkOffsetYMm, 3);
        Assert.Equal(0.15f, PrintLayoutLimits.DefaultWatermarkOpacity, 3);
    }

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00 }, ImageSignature.Png)]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, ImageSignature.Jpeg)]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38 }, null)]
    [InlineData(new byte[] { }, null)]
    public void An_image_is_recognised_by_its_first_bytes(byte[] bytes, string? expected)
    {
        Assert.Equal(expected, ImageSignature.DetectContentType(bytes));
    }

    private static PrintLayoutSettings Layout(double leftMarginCm) => new(
        LeftMarginCm: leftMarginCm,
        RightMarginCm: 1.0,
        TopMarginCm: 1.0,
        BottomMarginCm: 1.0,
        Logo: null,
        Signature: null);
}
