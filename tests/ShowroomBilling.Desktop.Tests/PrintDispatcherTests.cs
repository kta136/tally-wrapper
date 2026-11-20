using System.IO;
using System.IO.Packaging;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Xps.Packaging;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Printing;

namespace ShowroomBilling.Desktop.Tests;

/// <summary>
/// What a direct print hands to the print queue, read back from the XPS file
/// it would be sent as. No printer takes part: the dispatcher is given a
/// stand-in for the queue.
/// <para>
/// Set <c>TALLYWRAPPER_PRINT_SAMPLES</c> to a folder to keep the first test's
/// job there as <c>direct_print.xps</c>, with each page as the printer would
/// draw it at 300 DPI (<c>direct_print_page1.png</c>, …), to look at after a
/// change to the bill's layout or to how it is sent.
/// </para>
/// </summary>
public sealed class PrintDispatcherTests
{
    private static readonly byte[] Logo = Png(8, 4, Colors.DarkGoldenrod);
    private static readonly byte[] Signature = Png(6, 3, Colors.Navy);

    [Fact]
    public void A_bill_is_sent_as_text_and_lines_on_A4_with_a_page_for_each_copy()
    {
        var layout = PrintLayoutOptions.Default with { LogoBytes = Logo, SignatureBytes = Signature };

        var job = Print(PrintDocumentOptions.ForInvoice(Bill(), [CopyLabel.Original, CopyLabel.Duplicate], layout));
        KeepSample(job);

        Assert.Equal(2, job.Pages.Count);
        Assert.All(job.Pages, page =>
        {
            // A4 at 96 units an inch, as the picture pages always were.
            Assert.Equal("793.7", (string?)page.Root!.Attribute("Width"));
            Assert.Equal("1122.5", (string?)page.Root!.Attribute("Height"));
            Assert.Contains("INV/001", Texts(page));
            Assert.Contains("TAX INVOICE", Texts(page));
            Assert.NotEmpty(Elements(page, "Path"));
        });
        Assert.Contains("Original for Recipient", Texts(job.Pages[0]));
        Assert.Contains("Duplicate for Transporter", Texts(job.Pages[1]));

        // The logo and the signature, once each for both pages, and nothing
        // else: no picture of a page. (The signature was once written as a
        // second copy of the logo, which only the sizes here would have shown.)
        Assert.Equal(
            [(6, 3), (8, 4)],
            job.Images.Select(image => (image.PixelWidth, image.PixelHeight)).Order());
    }

    [Fact]
    public void Every_letter_of_the_bills_own_text_reaches_the_page()
    {
        // Letter pairs a font may join into one shape or pull together, and
        // the characters XML and XPS markup give a meaning of their own.
        var bill = Bill() with
        {
            PartyName = "{Office} & <Staff>",
            PartyAddress = "Waffle Tuft, ffi ffl AVATAR To.",
            Notes = "Two  spaces, a\ttab and ₹ 1,00,000",
        };

        var page = Print(PrintDocumentOptions.ForInvoice(bill, [CopyLabel.Original])).Pages[0];

        Assert.Contains("{Office} & <Staff>", Texts(page));
        Assert.Contains("Waffle Tuft, ffi ffl AVATAR To.", Texts(page));
        Assert.Contains(Texts(page), text => text.EndsWith("₹ 1,00,000", StringComparison.Ordinal));

        // One glyph for every letter of every run.
        Assert.All(Elements(page, "Glyphs"), glyphs =>
            Assert.Equal(
                Unescaped((string)glyphs.Attribute("UnicodeString")!).Length,
                ((string)glyphs.Attribute("Indices")!).Split(';').Length));
    }

    [Fact]
    public void Bold_text_is_drawn_with_the_bold_face_of_the_bills_font()
    {
        // Read from the pages before they are written: the XPS file holds its
        // fonts under made-up names.
        var faces = OnSta(() =>
        {
            var svg = new BillPrintRenderer().GeneratePageSvgs(PrintDocumentOptions.ForInvoice(Bill(), [CopyLabel.Original]));
            return ((Canvas)VectorPrintPages.Build(svg)[0]).Children.OfType<Glyphs>()
                .Select(glyphs => (glyphs.UnicodeString, Face: new GlyphTypeface(glyphs.FontUri)))
                .DistinctBy(run => run.UnicodeString)
                .ToDictionary(run => run.UnicodeString, run => (run.Face.Weight, Family: run.Face.FamilyNames.Values.First()));
        });

        // QuestPDF's SVG calls bold "600". If a new version calls it something
        // else, this is where it shows.
        Assert.Equal((FontWeights.Bold, "Arial"), faces["TAX INVOICE"]);
        Assert.Equal((FontWeights.Normal, "Arial"), faces["Bill To"]);
    }

    [Fact]
    public void A_watermark_is_sent_already_faded_with_nothing_see_through()
    {
        var layout = PrintLayoutOptions.Default with { WatermarkBytes = Png(4, 4, Colors.Red), WatermarkOpacity = 0.5f };

        var job = Print(PrintDocumentOptions.ForInvoice(Bill(), [CopyLabel.Original], layout));

        // Half-strength red on white paper, as one solid image under the text.
        Assert.NotEmpty(Elements(job.Pages[0], "Glyphs"));
        var watermark = Assert.Single(job.Images);
        var pixels = new byte[4 * 4 * 4];
        new FormatConvertedBitmap(watermark, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, 4 * 4, 0);
        Assert.All(pixels.Chunk(4), pixel => Assert.Equal([128, 128, 255, 255], pixel));
        Assert.DoesNotContain("Opacity", job.Pages[0].ToString());
    }

    [Fact]
    public void A_bill_with_letters_that_join_is_sent_as_pictures_of_its_pages()
    {
        // In the SVG a joined shape is left out without a trace, so the name
        // would print with letters missing.
        var bill = Bill() with { PartyName = "श्री राम" };
        var log = new ListLogger();

        var job = Print(PrintDocumentOptions.ForInvoice(bill, [CopyLabel.Original, CopyLabel.Duplicate]), log);

        Assert.Equal(2, job.Pages.Count);
        Assert.All(job.Pages, page => Assert.Empty(Elements(page, "Glyphs")));

        // A picture of each page, at the 600 DPI they have always had.
        Assert.Equal(2, job.Images.Count);
        Assert.All(job.Images, image => Assert.InRange(image.PixelWidth, 4955, 4965));

        // And a line in the log saying why this bill took longer.
        Assert.Contains(log.Warnings, warning => warning.Contains("pictures", StringComparison.Ordinal));
    }

    [Fact]
    public void A_job_the_printer_refuses_is_not_sent_a_second_time_as_pictures()
    {
        var sent = 0;
        var dispatcher = new PrintDispatcher(new BillPrintRenderer(), (_, _, _) =>
        {
            sent++;
            throw new InvalidOperationException("The printer is offline.");
        });

        var refused = Assert.Throws<InvalidOperationException>(() =>
            dispatcher.PrintToPrinter(PrintDocumentOptions.ForInvoice(Bill(), [CopyLabel.Original]), "Counter Printer"));

        Assert.Equal("The printer is offline.", refused.Message);
        Assert.Equal(1, sent);
    }

    [Fact]
    public void Nothing_is_sent_without_a_bill_or_a_printer()
    {
        var sent = 0;
        var dispatcher = new PrintDispatcher(new BillPrintRenderer(), (_, _, _) => sent++);

        Assert.False(dispatcher.PrintToPrinter(Array.Empty<PrintDocumentOptions>(), "Counter Printer"));
        Assert.False(dispatcher.PrintToPrinter(PrintDocumentOptions.ForInvoice(Bill(), [CopyLabel.Original]), " "));
        Assert.Equal(0, sent);
    }

    [Theory]
    [InlineData("a shape it does not draw", """<circle r="4"/>""")]
    [InlineData("a group", """<g><rect width="4" height="4"/></g>""")]
    [InlineData("a dashed line", """<path d="M0 0L9 0" fill="none" stroke="black" stroke-dasharray="2"/>""")]
    [InlineData("a rounded box", """<rect width="4" height="4" rx="1"/>""")]
    [InlineData("a font that is not installed", """<text font-size="9" font-family="No Such Font Family" x="0, " y="9, ">A</text>""")]
    [InlineData("a bold the font does not have", """<text font-size="9" font-weight="600" font-family="Segoe UI Symbol" x="0, " y="9, ">A</text>""")]
    [InlineData("a weight it does not know", """<text font-size="9" font-weight="700" font-family="Arial" x="0, " y="9, ">A</text>""")]
    [InlineData("fewer positions than letters", """<text font-size="9" font-family="Arial" x="0, " y="9, ">AB</text>""")]
    [InlineData("a letter the font does not have", """<text font-size="9" font-family="Arial" x="0, " y="9, ">&#x0936;</text>""")]
    [InlineData("a turned shape", """<rect width="4" height="4" transform="rotate(45)"/>""")]
    [InlineData("an image from somewhere else", """<defs><image id="a" width="4" height="4" xlink:href="https://example.invalid/a.png"/></defs>""")]
    public void A_page_holding_something_it_cannot_draw_exactly_is_refused(string what, string content)
    {
        var refused = Record.Exception(() => OnSta(() => VectorPrintPages.Build([SvgPage(content)]).Count));

        Assert.True(refused is NotSupportedException, $"A page with {what} was not refused: {refused?.Message ?? "it was drawn"}");
    }

    [Fact]
    public void A_page_that_is_not_A4_is_refused()
    {
        const string letterPaper = """<svg xmlns="http://www.w3.org/2000/svg" width="612" height="792"></svg>""";

        Assert.Throws<NotSupportedException>(() => OnSta(() => VectorPrintPages.Build([letterPaper]).Count));
    }

    [Fact]
    public void Letters_sit_where_the_page_puts_them_and_a_letter_further_left_starts_a_new_run()
    {
        // "A" and "B" ten points apart on one line, then "C" back at four.
        const string text = """<text transform="translate(5 7)" font-size="10" font-family="Arial" x="0, 10, 4, " y="8, ">ABC</text>""";

        var runs = OnSta(() => ((Canvas)VectorPrintPages.Build([SvgPage(text)])[0]).Children.OfType<Glyphs>()
            .Select(glyphs => (
                glyphs.UnicodeString,
                glyphs.OriginX,
                glyphs.OriginY,
                Advances: glyphs.Indices.Split(';').Select(index => index.Split(',').ElementAtOrDefault(1)).ToArray(),
                Offset: (glyphs.RenderTransform.Value.OffsetX, glyphs.RenderTransform.Value.OffsetY)))
            .ToList());

        Assert.Equal(2, runs.Count);
        // The advance from A to B: ten points at size ten is a hundred hundredths.
        Assert.Equal(("AB", 0d, 8d), (runs[0].UnicodeString, runs[0].OriginX, runs[0].OriginY));
        Assert.Equal(["100", null], runs[0].Advances);
        Assert.Equal(("C", 4d, 8d), (runs[1].UnicodeString, runs[1].OriginX, runs[1].OriginY));
        Assert.All(runs, run => Assert.Equal((5d, 7d), run.Offset));
    }

    private static string SvgPage(string content) =>
        $"""<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="595" height="842">{content}</svg>""";

    private static BillPrintContent Bill() =>
        PreviewSampleProvider.CreateSampleContent(new CompanyProfile(
            Name: "Shop",
            Address: "Town A",
            Phone: null,
            State: null,
            Country: null,
            Gstin: "GSTIN",
            Huid: null,
            BankName: "Bank",
            BankAccount: null,
            BankIfsc: null,
            BankUpi: null,
            TermsAndConditions: "Terms."))
        with
        {
            InvoiceNumber = "INV/001",
            PartyName = "Customer",
            PartyGstin = null,
            PartyPhone = null,
            PartyAddress = "Town A",
            Notes = null,
        };

    /// <summary>The job as the print queue would get it: its pages' markup and the images they use.</summary>
    private sealed record PrintedJob(byte[] Xps, IReadOnlyList<XDocument> Pages, IReadOnlyList<BitmapSource> Images);

    private static PrintedJob Print(PrintDocumentOptions bill, ILogger<PrintDispatcher>? logger = null)
    {
        byte[]? xps = null;
        var dispatcher = new PrintDispatcher(
            new BillPrintRenderer(),
            (document, _, _) =>
            {
                using var file = new MemoryStream();
                PrintDispatcher.WriteAsXps(document, file);
                xps = file.ToArray();
            },
            logger);

        Assert.True(dispatcher.PrintToPrinter(bill, "Counter Printer"));
        Assert.NotNull(xps);

        var pages = new SortedDictionary<int, XDocument>();
        var images = new List<BitmapSource>();
        using var package = Package.Open(new MemoryStream(xps), FileMode.Open, FileAccess.Read);
        foreach (var part in package.GetParts())
        {
            using var stream = part.GetStream();
            var name = part.Uri.ToString();
            if (name.EndsWith(".fpage", StringComparison.OrdinalIgnoreCase))
            {
                pages[int.Parse(Path.GetFileNameWithoutExtension(name))] = XDocument.Load(stream);
            }
            else if (part.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                copy.Position = 0;
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = copy;
                image.EndInit();
                image.Freeze();
                images.Add(image);
            }
        }

        return new PrintedJob(xps, pages.Values.ToList(), images);
    }

    private static IEnumerable<XElement> Elements(XDocument page, string name) =>
        page.Descendants().Where(element => element.Name.LocalName == name);

    private static List<string> Texts(XDocument page) =>
        Elements(page, "Glyphs").Select(glyphs => Unescaped((string)glyphs.Attribute("UnicodeString")!)).ToList();

    // XPS markup writes "{}" before a text that starts with a brace.
    private static string Unescaped(string text) => text.StartsWith("{}", StringComparison.Ordinal) ? text[2..] : text;

    private static byte[] Png(int width, int height, Color color)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (color.B, color.G, color.R, color.A);
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(
            BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static void KeepSample(PrintedJob job)
    {
        var folder = Environment.GetEnvironmentVariable("TALLYWRAPPER_PRINT_SAMPLES");
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "direct_print.xps");
        File.WriteAllBytes(file, job.Xps);

        // The pages as WPF draws the file, which is what an XPS printer does with it.
        OnSta(() =>
        {
            const double dpi = 300;
            using var document = new XpsDocument(file, FileAccess.Read);
            var paginator = document.GetFixedDocumentSequence().DocumentPaginator;
            for (var number = 0; number < paginator.PageCount; number++)
            {
                using var page = paginator.GetPage(number);
                var paper = new Rect(page.Size);
                var drawing = new DrawingVisual();
                using (var context = drawing.RenderOpen())
                {
                    context.DrawRectangle(Brushes.White, null, paper);
                    context.DrawRectangle(
                        new VisualBrush(page.Visual) { Stretch = Stretch.None, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = paper },
                        null,
                        paper);
                }

                var picture = new RenderTargetBitmap(
                    (int)Math.Round(paper.Width * dpi / 96), (int)Math.Round(paper.Height * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
                picture.Render(drawing);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(picture));
                using var png = File.Create(Path.Combine(folder, $"direct_print_page{number + 1}.png"));
                encoder.Save(png);
            }

            return 0;
        });
    }

    // WPF elements belong to the thread that made them, and want an STA one.
    private static T OnSta<T>(Func<T> action)
    {
        T result = default!;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        failure?.Throw();
        return result;
    }

    private sealed class ListLogger : ILogger<PrintDispatcher>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
