using System.IO;
using System.Printing;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media.Imaging;
using ShowroomBilling.Printing;

namespace ShowroomBilling.Desktop.Services;

public sealed class PrintDispatcher : IPrintDispatcher
{
    private const int PrintImageDpi = 600;
    private static readonly HashSet<char> InvalidFileNameChars = Path.GetInvalidFileNameChars().ToHashSet();

    private readonly IBillPrintRenderer _renderer;
    private readonly object _printerCacheGate = new();
    private IReadOnlyList<string>? _availablePrinters;
    private string? _defaultPrinter;

    public PrintDispatcher(IBillPrintRenderer renderer)
    {
        _renderer = renderer;
    }

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

            (_availablePrinters, _defaultPrinter) = LoadPrinters();
        }
    }

    private static (IReadOnlyList<string> Printers, string? DefaultPrinter) LoadPrinters()
    {
        try
        {
            using var server = new LocalPrintServer();
            using var defaultQueue = LocalPrintServer.GetDefaultPrintQueue();
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

                return (names, defaultQueue?.FullName);
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
            return (Array.Empty<string>(), null);
        }
    }

    public byte[] GeneratePdf(PrintDocumentOptions options) => _renderer.GeneratePdf(options);

    public IReadOnlyList<byte[]> GeneratePageImages(PrintDocumentOptions options, int dpi = 150)
        => _renderer.GeneratePageImages(options, dpi);

    public IReadOnlyList<byte[]> GeneratePrintPageImages(IReadOnlyList<PrintDocumentOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var pages = new List<byte[]>();
        foreach (var option in options)
        {
            if (option is null) continue;
            pages.AddRange(_renderer.GeneratePageImages(option, imageDpi: PrintImageDpi));
        }

        return pages;
    }

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
        if (options.Count == 0) return false;

        var pages = GeneratePrintPageImages(options);
        return PrintRenderedPages(pages, printerName, settings);
    }

    public bool PrintRenderedPages(
        IReadOnlyList<byte[]> pageImages,
        string printerName,
        PrintJobSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(pageImages);
        if (string.IsNullOrWhiteSpace(printerName)) return false;
        if (pageImages.Count == 0) return false;

        return RunOnSta(() =>
        {
            using var server = new LocalPrintServer();
            using var queue = server.GetPrintQueue(printerName);
            var doc = BuildFixedDocument(pageImages);
            var writer = PrintQueue.CreateXpsDocumentWriter(queue);
            var ticket = TryBuildPrintTicket(queue, settings ?? PrintJobSettings.Default);
            if (ticket is null)
            {
                writer.Write(doc);
            }
            else
            {
                writer.Write(doc, ticket);
            }
            return true;
        });
    }

    public string SavePdfToDisk(PrintDocumentOptions options, string directory, string fileNameWithoutExtension)
    {
        Directory.CreateDirectory(directory);
        var safeName = string.Concat(fileNameWithoutExtension.Select(c => InvalidFileNameChars.Contains(c) ? '_' : c));
        var fullPath = Path.Combine(directory, safeName + ".pdf");
        var bytes = _renderer.GeneratePdf(options);
        File.WriteAllBytes(fullPath, bytes);
        return fullPath;
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

    private static FixedDocument BuildFixedDocument(IReadOnlyList<byte[]> pageImages)
    {
        // A4 at 96 DPI: 8.27" x 11.69" -> 793.7 x 1122.5
        const double pageWidth = 793.7;
        const double pageHeight = 1122.5;

        var doc = new FixedDocument();
        doc.DocumentPaginator.PageSize = new Size(pageWidth, pageHeight);

        foreach (var bytes in pageImages)
        {
            var bmp = new BitmapImage();
            using var stream = new MemoryStream(bytes);
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = stream;
            bmp.EndInit();
            bmp.Freeze();

            var image = new Image
            {
                Source = bmp,
                Stretch = System.Windows.Media.Stretch.Uniform,
                Width = pageWidth,
                Height = pageHeight,
            };

            var page = new FixedPage { Width = pageWidth, Height = pageHeight };
            page.Children.Add(image);
            FixedPage.SetLeft(image, 0);
            FixedPage.SetTop(image, 0);

            var content = new PageContent();
            ((IAddChild)content).AddChild(page);
            doc.Pages.Add(content);
        }

        return doc;
    }

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
