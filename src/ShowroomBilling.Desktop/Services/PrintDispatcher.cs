using System.IO;
using System.IO.Packaging;
using System.Printing;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media.Imaging;
using System.Windows.Xps.Packaging;
using Microsoft.Extensions.Logging;
using ShowroomBilling.Printing;

namespace ShowroomBilling.Desktop.Services;

public sealed class PrintDispatcher : IPrintDispatcher
{
    private const int PrintImageDpi = 600;

    // A4 at 96 DPI: 8.27" x 11.69" -> 793.7 x 1122.5
    private const double PageWidth = 793.7;
    private const double PageHeight = 1122.5;

    private readonly BillPrintRenderer _renderer;
    private readonly Action<FixedDocument, string, PrintJobSettings> _sendToPrinter;
    private readonly ILogger<PrintDispatcher>? _logger;
    private readonly object _printerCacheGate = new();
    private IReadOnlyList<string>? _availablePrinters;
    private string? _defaultPrinter;
    private volatile FadedWatermark? _fadedWatermark;

    public PrintDispatcher(BillPrintRenderer renderer, ILogger<PrintDispatcher>? logger = null)
        : this(renderer, SendToPrintQueue, logger)
    {
    }

    /// <summary>For a test: the finished document is handed to <paramref name="sendToPrinter"/> instead of a print queue.</summary>
    internal PrintDispatcher(
        BillPrintRenderer renderer,
        Action<FixedDocument, string, PrintJobSettings> sendToPrinter,
        ILogger<PrintDispatcher>? logger = null)
    {
        _renderer = renderer;
        _sendToPrinter = sendToPrinter;
        _logger = logger;
    }

    private sealed record FadedWatermark(byte[] Source, float Opacity, byte[] Image);

    public IReadOnlyList<string> AvailablePrinters()
    {
        EnsurePrinterCache();
        return _availablePrinters ?? Array.Empty<string>();
    }

    public string? DefaultPrinter()
    {
        EnsurePrinterCache();
        return _defaultPrinter;
    }

    public PrintJobCapabilities GetPrinterCapabilities(string printerName)
    {
        if (string.IsNullOrWhiteSpace(printerName))
        {
            return PrintJobCapabilities.Unknown;
        }

        try
        {
            return RunOnSta(() =>
            {
                using var server = new LocalPrintServer();
                using var queue = server.GetPrintQueue(printerName);
                var capabilities = queue.GetPrintCapabilities();

                var duplexModes = new List<PrintDuplexMode>
                {
                    PrintDuplexMode.PrinterDefault,
                    PrintDuplexMode.OneSided,
                };
                if (capabilities.DuplexingCapability.Contains(Duplexing.TwoSidedLongEdge))
                {
                    duplexModes.Add(PrintDuplexMode.TwoSidedLongEdge);
                }
                if (capabilities.DuplexingCapability.Contains(Duplexing.TwoSidedShortEdge))
                {
                    duplexModes.Add(PrintDuplexMode.TwoSidedShortEdge);
                }

                var colorModes = new List<PrintColorMode> { PrintColorMode.PrinterDefault };
                if (capabilities.OutputColorCapability.Contains(OutputColor.Color))
                {
                    colorModes.Add(PrintColorMode.Color);
                }
                if (capabilities.OutputColorCapability.Contains(OutputColor.Monochrome))
                {
                    colorModes.Add(PrintColorMode.Monochrome);
                }

                var collationModes = new List<PrintCollationMode> { PrintCollationMode.PrinterDefault };
                if (capabilities.CollationCapability.Contains(Collation.Collated))
                {
                    collationModes.Add(PrintCollationMode.Collated);
                }
                if (capabilities.CollationCapability.Contains(Collation.Uncollated))
                {
                    collationModes.Add(PrintCollationMode.Uncollated);
                }

                return new PrintJobCapabilities(
                    IsKnown: true,
                    DuplexModes: duplexModes,
                    ColorModes: colorModes,
                    CollationModes: collationModes);
            });
        }
        catch
        {
            return PrintJobCapabilities.Unknown;
        }
    }

    public bool PrintsOnBothSides(string printerName, PrintJobSettings? settings = null)
    {
        switch ((settings ?? PrintJobSettings.Default).Duplex)
        {
            case PrintDuplexMode.OneSided:
                return false;
            case PrintDuplexMode.TwoSidedLongEdge:
            case PrintDuplexMode.TwoSidedShortEdge:
                return true;
        }

        // "Printer default": what the printer is set to for this Windows user, which is the
        // ticket a job with no setting of its own is printed with. Read each time, not kept:
        // it can be changed in Windows while the app runs.
        try
        {
            return RunOnSta(() =>
            {
                using var server = new LocalPrintServer();
                using var queue = server.GetPrintQueue(printerName);
                return (queue.UserPrintTicket ?? queue.DefaultPrintTicket)?.Duplexing switch
                {
                    Duplexing.OneSided => false,
                    Duplexing.TwoSidedLongEdge or Duplexing.TwoSidedShortEdge => true,
                    // It does not say. A printer that cannot print on both sides at all says nothing.
                    _ => queue.GetPrintCapabilities().DuplexingCapability
                        .Any(side => side is Duplexing.TwoSidedLongEdge or Duplexing.TwoSidedShortEdge),
                };
            });
        }
        catch
        {
            return true;
        }
    }

    public void RefreshPrinters()
    {
        // A read that fails keeps the last good list: better than showing none.
        if (LoadPrinters() is { } loaded)
        {
            lock (_printerCacheGate)
            {
                (_availablePrinters, _defaultPrinter) = loaded;
            }
        }
    }

    private void EnsurePrinterCache()
    {
        if (_availablePrinters is not null)
        {
            return;
        }

        lock (_printerCacheGate)
        {
            if (_availablePrinters is not null)
            {
                return;
            }

            // A failed read is not kept, so the next call tries again. Kept, it
            // meant "no printers" until the app was restarted.
            if (LoadPrinters() is { } loaded)
            {
                (_availablePrinters, _defaultPrinter) = loaded;
            }
        }
    }

    /// <summary>The installed printers and the default one, or null when Windows could not list them.</summary>
    private static (IReadOnlyList<string> Printers, string? DefaultPrinter)? LoadPrinters()
    {
        try
        {
            using var server = new LocalPrintServer();
            var queues = server.GetPrintQueues(new[]
            {
                EnumeratedPrintQueueTypes.Local,
                EnumeratedPrintQueueTypes.Connections,
            });
            try
            {
                var names = queues
                    .Select(q => q.FullName)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return (names, ReadDefaultPrinter());
            }
            finally
            {
                foreach (var queue in queues)
                {
                    try { queue.Dispose(); } catch { }
                }
            }
        }
        catch
        {
            return null;
        }
    }

    // Read apart from the list: a PC with printers but no default one still has a list to show.
    private static string? ReadDefaultPrinter()
    {
        try
        {
            using var defaultQueue = LocalPrintServer.GetDefaultPrintQueue();
            return defaultQueue?.FullName;
        }
        catch
        {
            return null;
        }
    }

    public byte[] GeneratePdf(PrintDocumentOptions options) => _renderer.GeneratePdf(options);

    public IReadOnlyList<byte[]> GeneratePageImages(PrintDocumentOptions options, int dpi = 150)
        => _renderer.GeneratePageImages(options, dpi);

    public bool PrintToPrinter(PrintDocumentOptions options, string printerName, PrintJobSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        return PrintToPrinter([options], printerName, settings);
    }

    public bool PrintToPrinter(
        IReadOnlyList<PrintDocumentOptions> options,
        string printerName,
        PrintJobSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(printerName)) return false;

        var bills = options.Where(option => option is not null).ToList();
        if (bills.Count == 0) return false;

        return RunOnSta(() =>
        {
            var document = BuildPrintDocument(bills);
            if (document.Pages.Count == 0)
            {
                return false;
            }

            _sendToPrinter(document, printerName, settings ?? PrintJobSettings.Default);
            return true;
        });
    }

    /// <summary>
    /// The bills as the document that goes to the printer, one page for each
    /// copy: text and lines where a bill can be drawn that way exactly, and
    /// otherwise a 600 DPI picture of every page, as all printing once was.
    /// Needs an STA thread.
    /// </summary>
    internal FixedDocument BuildPrintDocument(IReadOnlyList<PrintDocumentOptions> bills)
    {
        try
        {
            var document = BuildVectorDocument(bills);
            if (document.Pages.Count > 0)
            {
                return document;
            }
        }
        catch (Exception ex)
        {
            // Why a bill came out slower, as pictures, is worth being able to look up.
            _logger?.LogWarning(ex, "A bill could not be sent to the printer as text and lines; sending pictures of its pages instead.");
        }

        return BuildPictureDocument(bills);
    }

    private FixedDocument BuildVectorDocument(IReadOnlyList<PrintDocumentOptions> bills)
    {
        var pages = new List<string>();
        foreach (var bill in bills)
        {
            VectorPrintPages.RefuseTextBeyondPlainLetters(bill.Content);
            pages.AddRange(_renderer.GeneratePageSvgs(WithWatermarkFaded(bill)));
        }

        var document = ToDocument(VectorPrintPages.Build(pages));

        // Whatever in it cannot be written as XPS (a font that will not
        // embed, say) has to show before the printer has been sent anything:
        // after that, falling back to pictures could print a bill twice.
        WriteAsXps(document, Stream.Null);
        return document;
    }

    /// <summary>
    /// An SVG page leaves out what is drawn half-transparent, so the watermark
    /// is handed over as it looks on white paper, at full strength.
    /// </summary>
    private PrintDocumentOptions WithWatermarkFaded(PrintDocumentOptions bill)
    {
        var layout = bill.Layout;
        if (layout.WatermarkBytes is not { Length: > 0 } watermark || layout.WatermarkOpacity <= 0f)
        {
            return bill;
        }

        // The same file at the same strength bill after bill: fade it once.
        var faded = _fadedWatermark;
        if (faded is null || !ReferenceEquals(faded.Source, watermark) || faded.Opacity != layout.WatermarkOpacity)
        {
            faded = new FadedWatermark(
                watermark,
                layout.WatermarkOpacity,
                VectorPrintPages.FadeOntoWhite(watermark, layout.WatermarkOpacity));
            _fadedWatermark = faded;
        }

        return bill with { Layout = layout with { WatermarkBytes = faded.Image, WatermarkOpacity = 1f } };
    }

    private FixedDocument BuildPictureDocument(IReadOnlyList<PrintDocumentOptions> bills)
    {
        var pictures = new List<byte[]>();
        foreach (var bill in bills)
        {
            pictures.AddRange(_renderer.GeneratePageImages(bill, imageDpi: PrintImageDpi));
        }

        return BuildFixedDocument(pictures);
    }

    /// <summary>A document with an A4 page for each picture (a PNG of a whole page).</summary>
    internal static FixedDocument BuildFixedDocument(IReadOnlyList<byte[]> pageImages)
    {
        var pages = new List<UIElement>(pageImages.Count);
        foreach (var bytes in pageImages)
        {
            // Not unpacked here. A page at 600 DPI is about 140 MB once unpacked, and a job
            // of several bills held every page that way at once (1.7 GB for six pages). With
            // no cache the picture is read from its PNG only while its page is written, so
            // what a job needs no longer grows with its pages. The stream is left open for that.
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.None;
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.EndInit();
            bmp.Freeze();

            pages.Add(new Image
            {
                Source = bmp,
                Stretch = System.Windows.Media.Stretch.Uniform,
                Width = PageWidth,
                Height = PageHeight,
            });
        }

        return ToDocument(pages);
    }

    private static FixedDocument ToDocument(IEnumerable<UIElement> pages)
    {
        var doc = new FixedDocument();
        doc.DocumentPaginator.PageSize = new Size(PageWidth, PageHeight);

        foreach (var element in pages)
        {
            var page = new FixedPage { Width = PageWidth, Height = PageHeight };
            page.Children.Add(element);
            FixedPage.SetLeft(element, 0);
            FixedPage.SetTop(element, 0);

            var content = new PageContent();
            ((IAddChild)content).AddChild(page);
            doc.Pages.Add(content);
        }

        return doc;
    }

    /// <summary>Writes the document as an XPS file: what a print queue is sent, without the queue.</summary>
    internal static void WriteAsXps(FixedDocument document, Stream target)
    {
        using var buffer = new MemoryStream();
        using (var package = Package.Open(buffer, FileMode.Create, FileAccess.ReadWrite))
        {
            // An XPS document over a stream has to be findable by a pack URI while it is written.
            var address = new Uri($"pack://print-{Guid.NewGuid():N}.xps");
            PackageStore.AddPackage(address, package);
            try
            {
                using var xps = new XpsDocument(package, CompressionOption.Fast, address.AbsoluteUri);
                XpsDocument.CreateXpsDocumentWriter(xps).Write(document);
            }
            finally
            {
                PackageStore.RemovePackage(address);
            }
        }

        buffer.Position = 0;
        buffer.CopyTo(target);
    }

    private static void SendToPrintQueue(FixedDocument document, string printerName, PrintJobSettings settings)
    {
        using var server = new LocalPrintServer();
        using var queue = server.GetPrintQueue(printerName);
        var writer = PrintQueue.CreateXpsDocumentWriter(queue);
        var ticket = TryBuildPrintTicket(queue, settings);
        if (ticket is null)
        {
            writer.Write(document);
        }
        else
        {
            writer.Write(document, ticket);
        }
    }

    private static PrintTicket? TryBuildPrintTicket(PrintQueue queue, PrintJobSettings settings)
    {
        if (settings.IsPrinterDefault)
        {
            return null;
        }

        try
        {
            var delta = new PrintTicket();
            var hasSettings = false;

            if (MapDuplex(settings.Duplex) is { } duplex)
            {
                delta.Duplexing = duplex;
                hasSettings = true;
            }
            if (MapColor(settings.Color) is { } color)
            {
                delta.OutputColor = color;
                hasSettings = true;
            }
            if (MapCollation(settings.Collation) is { } collation)
            {
                delta.Collation = collation;
                hasSettings = true;
            }

            if (!hasSettings)
            {
                return null;
            }

            var baseTicket = queue.UserPrintTicket ?? queue.DefaultPrintTicket ?? new PrintTicket();
            return queue.MergeAndValidatePrintTicket(baseTicket, delta).ValidatedPrintTicket;
        }
        catch
        {
            return null;
        }
    }

    private static Duplexing? MapDuplex(PrintDuplexMode mode) => mode switch
    {
        PrintDuplexMode.OneSided => Duplexing.OneSided,
        PrintDuplexMode.TwoSidedLongEdge => Duplexing.TwoSidedLongEdge,
        PrintDuplexMode.TwoSidedShortEdge => Duplexing.TwoSidedShortEdge,
        _ => null,
    };

    private static OutputColor? MapColor(PrintColorMode mode) => mode switch
    {
        PrintColorMode.Color => OutputColor.Color,
        PrintColorMode.Monochrome => OutputColor.Monochrome,
        _ => null,
    };

    private static Collation? MapCollation(PrintCollationMode mode) => mode switch
    {
        PrintCollationMode.Collated => Collation.Collated,
        PrintCollationMode.Uncollated => Collation.Uncollated,
        _ => null,
    };

    // LocalPrintServer, PrintQueue, and WPF layout objects (FixedDocument, FixedPage, Image)
    // all require an STA thread. Task.Run uses MTA thread-pool threads, so we spin up a
    // short-lived background STA thread and join it before returning.
    private static T RunOnSta<T>(Func<T> action)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            return action();

        T result = default!;
        ExceptionDispatchInfo? caught = null;
        var thread = new Thread(() =>
        {
            try { result = action(); }
            catch (Exception ex) { caught = ExceptionDispatchInfo.Capture(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        caught?.Throw();
        return result;
    }
}
