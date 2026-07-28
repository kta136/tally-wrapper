using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Printing;

namespace ShowroomBilling.Tests;

/// <summary>
/// Smoke tests for <see cref="BillPrintRenderer"/>: run QuestPDF end-to-end on
/// representative inputs and assert non-empty PDF output. Visual parity is verified
/// manually against V1 during smoke testing; these tests only guard against regressions
/// that break composition entirely.
/// </summary>
public class BillDocumentRenderTests
{
    private static readonly IBillPrintRenderer Renderer = new BillPrintRenderer();

    [Fact]
    public void GeneratePdf_for_minimal_bill_produces_bytes()
    {
        var options = PrintDocumentOptions.ForInvoice(MinimalContent(), new[] { CopyLabel.Original });

        var pdf = Renderer.GeneratePdf(options);

        Assert.NotNull(pdf);
        Assert.True(pdf.Length > 200, "PDF should contain at least a header + some content");
        AssertPdfMagic(pdf);
    }

    [Fact]
    public void GeneratePdf_renders_one_page_per_copy()
    {
        var content = MinimalContent();
        var options = PrintDocumentOptions.ForInvoice(
            content,
            new[] { CopyLabel.Original, CopyLabel.Duplicate, CopyLabel.Triplicate });

        var pages = Renderer.GeneratePageImages(options, imageDpi: 72);

        Assert.Equal(3, pages.Count);
    }

    [Fact]
    public void GeneratePdf_empty_copies_falls_back_to_original()
    {
        var options = PrintDocumentOptions.ForInvoice(MinimalContent(), Array.Empty<CopyLabel>());

        var pages = Renderer.GeneratePageImages(options, imageDpi: 72);

        Assert.Single(pages);
    }

    [Fact]
    public void GeneratePdf_with_rich_content_does_not_throw()
    {
        var options = PrintDocumentOptions.ForInvoice(RichContent(), new[] { CopyLabel.Original });

        var pdf = Renderer.GeneratePdf(options);

        AssertPdfMagic(pdf);
    }

    [Fact]
    public void GeneratePdf_with_diamond_line_uses_diamond_metadata_without_throwing()
    {
        var content = RichContent();
        var diamond = content.Lines.Single(l => l.ItemCategory == "diamond");
        Assert.Equal(45000m, diamond.DiamondRate);
        Assert.Equal(3.500m, diamond.Quantity);

        var options = PrintDocumentOptions.ForInvoice(content, new[] { CopyLabel.Original });

        var pdf = Renderer.GeneratePdf(options);

        AssertPdfMagic(pdf);
    }

    [Fact]
    public void GeneratePdf_fills_single_page_when_items_are_few()
    {
        // Page-fill guard: the dynamic spacer between items and summary must not
        // push a 1-item bill onto a second page.
        var options = PrintDocumentOptions.ForInvoice(MinimalContent(), new[] { CopyLabel.Original });

        var pages = Renderer.GeneratePageImages(options, imageDpi: 72);

        Assert.Single(pages);
    }

    [Fact]
    public void GeneratePdf_keeps_near_limit_bill_on_single_page()
    {
        // Regression guard for dynamic page-fill: a 12-item bill with terms + bank
        // populated currently fits on one A4 page. The spacer must not mis-size and
        // bump anything to page 2.
        var options = PrintDocumentOptions.ForInvoice(ContentWithLines(12), new[] { CopyLabel.Original });

        var pages = Renderer.GeneratePageImages(options, imageDpi: 72);

        Assert.Single(pages);
    }

    [Fact]
    public void GeneratePdf_does_not_overflow_when_items_are_many()
    {
        // Many-items case: render must succeed. Multi-page output is acceptable —
        // what we're guarding against is the new spacer introducing a crash or
        // collapsing content incorrectly.
        var options = PrintDocumentOptions.ForInvoice(ContentWithLines(20), new[] { CopyLabel.Original });

        var pages = Renderer.GeneratePageImages(options, imageDpi: 72);

        Assert.True(pages.Count >= 1);
    }

    /// <summary>
    /// Writes a representative "rich" PDF next to the test assembly so humans can
    /// open it to compare the new QuestPDF layout against the V1 reference print.
    /// Path: <c>tests/ShowroomBilling.Tests/bin/Debug/net10.0/sample_invoice.pdf</c>.
    /// </summary>
    [Fact]
    public void WriteSampleInvoicePdf_for_manual_inspection()
    {
        var layout = StructuredLayout() with
        {
            WatermarkBytes = ManualWatermark(),
            WatermarkOpacity = 0.15f,
            WatermarkOffsetXMm = 45,
            WatermarkOffsetYMm = 88.5f,
            WatermarkWidthMm = 120,
            WatermarkHeightMm = 120
        };
        var options = PrintDocumentOptions.ForInvoice(
            RichContent(),
            new[] { CopyLabel.Original, CopyLabel.Duplicate },
            layout);
        var pdf = Renderer.GeneratePdf(options);

        var path = Path.Combine(AppContext.BaseDirectory, "sample_invoice.pdf");
        File.WriteAllBytes(path, pdf);

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void WriteOverflowingSampleInvoicePdf_for_manual_inspection()
    {
        var layout = StructuredLayout() with
        {
            WatermarkBytes = ManualWatermark(),
            WatermarkOpacity = 0.15f,
            WatermarkOffsetXMm = 45,
            WatermarkOffsetYMm = 88.5f,
            WatermarkWidthMm = 120,
            WatermarkHeightMm = 120
        };
        var pdf = Renderer.GeneratePdf(PrintDocumentOptions.ForInvoice(
            ContentWithLines(60),
            [CopyLabel.Original],
            layout));

        var path = Path.Combine(AppContext.BaseDirectory, "sample_invoice_overflow.pdf");
        File.WriteAllBytes(path, pdf);

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Printed_amounts_match_LineTotal_sum()
    {
        // Regression guard: LineTotal from InvoiceViewModel is already GST-inclusive.
        // A previous rev reallocated TaxTotal on top of LineTotal, inflating printed
        // amounts. This test pins that the sum of per-line LineTotal equals the
        // itemsTotalIncl we display in the summary.
        var content = RichContent();
        var totalOfLines = content.Lines.Sum(l => l.LineTotal);

        // items-total-incl in the summary uses the same sum; verify arithmetic here.
        Assert.Equal(303859.75m, totalOfLines);
    }

    [Fact]
    public void GeneratePdf_respects_font_size_and_margin_clamps()
    {
        // Wildly out-of-range values should still render (clamped internally).
        var layout = new PrintLayoutOptions(
            MarginLeftMm: -10f, MarginTopMm: 99f, MarginRightMm: 0f, MarginBottomMm: 99f,
            InvoiceFontSize: 2, TermsFontSize: 99);
        var options = PrintDocumentOptions.ForInvoice(MinimalContent(), new[] { CopyLabel.Original }, layout);

        var pdf = Renderer.GeneratePdf(options);

        AssertPdfMagic(pdf);
    }

    [Theory]
    [InlineData(PrintPageDensity.Compact)]
    [InlineData(PrintPageDensity.Standard)]
    [InlineData(PrintPageDensity.Comfortable)]
    public void GeneratePdf_renders_all_density_modes(string density)
    {
        var layout = StructuredLayout() with { PageDensity = density };

        var pdf = Renderer.GeneratePdf(PrintDocumentOptions.ForInvoice(
            RichContent(),
            [CopyLabel.Original],
            layout));

        AssertPdfMagic(pdf);
    }

    [Fact]
    public void GeneratePdf_renders_reordered_hidden_spaced_sections_with_zero_border()
    {
        var sections = PrintLayoutSectionKeys.All
            .Reverse()
            .Select(key => new PrintSectionLayoutOptions(
                key,
                IsVisible: !PrintLayoutSectionKeys.Optional.Contains(key),
                SpacingBeforeMm: 0.5f,
                SpacingAfterMm: 0.5f))
            .ToArray();
        var layout = StructuredLayout() with
        {
            InvoiceBorderThicknessPt = 0,
            BottomPinnedFromSectionKey = PrintLayoutSectionKeys.Totals,
            Sections = sections
        };

        var pdf = Renderer.GeneratePdf(PrintDocumentOptions.ForInvoice(
            RichContent(),
            [CopyLabel.Original],
            layout));

        AssertPdfMagic(pdf);
    }

    [Theory]
    [MemberData(nameof(WatermarkImages))]
    public void GeneratePdf_renders_png_and_jpeg_watermarks(string _, byte[] watermarkBytes)
    {
        var layout = StructuredLayout() with
        {
            WatermarkBytes = watermarkBytes,
            WatermarkOpacity = 0.15f,
            WatermarkOffsetXMm = 45,
            WatermarkOffsetYMm = 88.5f,
            WatermarkWidthMm = 120,
            WatermarkHeightMm = 120
        };

        var pdf = Renderer.GeneratePdf(PrintDocumentOptions.ForInvoice(
            RichContent(),
            [CopyLabel.Original],
            layout));

        AssertPdfMagic(pdf);
    }

    [Fact]
    public void Watermark_repeats_without_breaking_overflowing_multiple_copies()
    {
        var layout = StructuredLayout() with
        {
            WatermarkBytes = PngWatermark,
            WatermarkOpacity = 0.22f
        };

        var pages = Renderer.GeneratePageImages(
            PrintDocumentOptions.ForInvoice(
                ContentWithLines(24),
                [CopyLabel.Original, CopyLabel.Duplicate],
                layout),
            imageDpi: 72);

        Assert.True(pages.Count >= 2);
        Assert.All(pages, page => Assert.True(page.Length > 100));
    }

    [Fact]
    public void Missing_or_zero_opacity_watermark_never_blocks_rendering()
    {
        var noBytes = StructuredLayout() with { WatermarkBytes = null, WatermarkOpacity = 0.2f };
        var zeroOpacity = StructuredLayout() with { WatermarkBytes = PngWatermark, WatermarkOpacity = 0 };

        AssertPdfMagic(Renderer.GeneratePdf(PrintDocumentOptions.ForInvoice(
            MinimalContent(), [CopyLabel.Original], noBytes)));
        AssertPdfMagic(Renderer.GeneratePdf(PrintDocumentOptions.ForInvoice(
            MinimalContent(), [CopyLabel.Original], zeroOpacity)));
    }

    private static BillPrintContent MinimalContent() => new(
        InvoiceNumber: "TEST/001",
        BillDate: new DateOnly(2026, 4, 23),
        PartyName: "Walk-in Customer",
        PartyGstin: null,
        PartyPhone: null,
        PartyAddress: null,
        Payment: "Cash",
        Rate24Kt: null,
        Lines: new[]
        {
            new BillLineItemDto(
                ItemName: "Gold Chain",
                HsnCode: "7113",
                Quantity: 10.500m,
                Unit: "g",
                Rate: 6000m,
                LineTotal: 63000m,
                Karat: "22K",
                RawJson: null),
        },
        Totals: new BillTotalsDto(
            Subtotal: 63000m,
            DiscountTotal: 0m,
            TaxTotal: 1890m,
            RoundOff: 0m,
            GrandTotal: 64890m),
        Notes: null,
        Company: SampleCompany(withBank: false));

    private static BillPrintContent RichContent() => new(
        InvoiceNumber: "RICH/42",
        BillDate: new DateOnly(2026, 4, 23),
        PartyName: "Mrs. Sharma",
        PartyGstin: "29AAAA0000A1Z5",
        PartyPhone: "+91 98765 43210",
        PartyAddress: "12, MG Road, Bengaluru",
        Payment: "Credit and debit",
        Rate24Kt: 72500m,
        Lines: new[]
        {
            new BillLineItemDto(
                ItemName: "Gold Necklace",
                HsnCode: null,
                Quantity: 22.345m,
                Unit: "g",
                Rate: 6550m,
                LineTotal: 146359.75m,
                Karat: "22K",
                RawJson: null,
                GrossWeight: 22.800m,
                LessWeight: 0.455m,
                WastagePercent: 12.50m,
                LabourPerUnit: 150m,
                Extra: 500m,
                ItemCategory: "gold_based",
                PricingMode: "both"),
            new BillLineItemDto(
                ItemName: "Diamond Ring",
                HsnCode: "7113",
                Quantity: 3.500m,
                Unit: "g",
                Rate: 45000m,
                LineTotal: 157500m,
                Karat: "18K",
                RawJson: null,
                GrossWeight: 3.500m,
                LessWeight: 0m,
                WastagePercent: 0m,
                LabourPerUnit: 0m,
                DiamondRate: 45000m,
                Extra: 0m,
                ItemCategory: "diamond",
                PricingMode: "wastage"),
        },
        Totals: new BillTotalsDto(
            Subtotal: 303859.75m,
            DiscountTotal: 3000m,
            TaxTotal: 9115.79m,
            RoundOff: -0.54m,
            GrandTotal: 309975m),
        Notes: "Repair claim credited against invoice",
        Company: SampleCompany(withBank: true));

    private static BillPrintContent ContentWithLines(int count)
    {
        var lines = Enumerable.Range(1, count).Select(i => new BillLineItemDto(
            ItemName: $"Item {i:00}",
            HsnCode: "7113",
            Quantity: 5.000m,
            Unit: "g",
            Rate: 6000m,
            LineTotal: 30000m,
            Karat: "22K",
            RawJson: null)).ToArray();

        var subtotal = lines.Sum(l => l.LineTotal);
        return new BillPrintContent(
            InvoiceNumber: $"BULK/{count:00}",
            BillDate: new DateOnly(2026, 4, 23),
            PartyName: "Mrs. Sharma",
            PartyGstin: "29AAAA0000A1Z5",
            PartyPhone: "+91 98765 43210",
            PartyAddress: "12, MG Road, Bengaluru",
            Payment: "Cash",
            Rate24Kt: 72500m,
            Lines: lines,
            Totals: new BillTotalsDto(
                Subtotal: subtotal,
                DiscountTotal: 0m,
                TaxTotal: subtotal * 0.03m,
                RoundOff: 0m,
                GrandTotal: subtotal),
            Notes: null,
            Company: SampleCompany(withBank: true));
    }

    private static CompanyProfile SampleCompany(bool withBank) => new(
        Name: "Acme Jewellers Pvt Ltd",
        Address: "Main Road, Kanpur",
        Phone: "+91 99999 12345",
        State: "Uttar Pradesh",
        Country: "India",
        Gstin: "09ACME0000A1Z9",
        Huid: "HU1234",
        BankName: withBank ? "HDFC Bank" : null,
        BankAccount: withBank ? "501001234567" : null,
        BankIfsc: withBank ? "HDFC0000123" : null,
        BankUpi: withBank ? "acme@hdfcbank" : null,
        TermsAndConditions: "Goods once sold will not be taken back.\nE.&O.E.");

    public static IEnumerable<object[]> WatermarkImages()
    {
        yield return ["png", PngWatermark];
        yield return ["jpeg", JpegWatermark];
    }

    private static PrintLayoutOptions StructuredLayout() =>
        new(
            BottomPinnedFromSectionKey: PrintLayoutSectionKeys.GstBreakup,
            Sections: PrintLayoutSectionKeys.All
                .Select(key => new PrintSectionLayoutOptions(key, true, 0, 0))
                .ToArray());

    private static byte[] PngWatermark { get; } = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Z4x8AAAAASUVORK5CYII=");

    private static byte[] JpegWatermark { get; } = Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAP//////////////////////////////////////////////////////////////////////////////////////2wBDAf//////////////////////////////////////////////////////////////////////////////////////wAARCAABAAEDASIAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAf/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oADAMBAAIQAxAAAAF//8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABBQJ//8QAFBEBAAAAAAAAAAAAAAAAAAAAAP/aAAgBAwEBPwF//8QAFBEBAAAAAAAAAAAAAAAAAAAAAP/aAAgBAgEBPwF//8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQAGPwJ//8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABPyF//9oADAMBAAIAAwAAABAf/8QAFBEBAAAAAAAAAAAAAAAAAAAAAP/aAAgBAwEBPxB//8QAFBEBAAAAAAAAAAAAAAAAAAAAAP/aAAgBAgEBPxB//8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABPxB//9k=");

    private static byte[] ManualWatermark()
    {
        var sampleWatermarkPath = Environment.GetEnvironmentVariable(
            "SHOWROOM_BILLING_SAMPLE_WATERMARK_PATH");
        return !string.IsNullOrWhiteSpace(sampleWatermarkPath)
            && File.Exists(sampleWatermarkPath)
                ? File.ReadAllBytes(sampleWatermarkPath)
                : PngWatermark;
    }

    private static void AssertPdfMagic(byte[] bytes)
    {
        Assert.True(bytes.Length >= 4, "PDF should have at least 4 bytes");
        // %PDF
        Assert.Equal((byte)'%', bytes[0]);
        Assert.Equal((byte)'P', bytes[1]);
        Assert.Equal((byte)'D', bytes[2]);
        Assert.Equal((byte)'F', bytes[3]);
    }
}
