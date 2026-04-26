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

    public bool PrintToPrinter(PrintDocumentOptions options, string printerName)
    {
        if (string.IsNullOrWhiteSpace(printerName)) return false;

        var pages = _renderer.GeneratePageImages(options, imageDpi: PrintImageDpi);
        if (pages.Count == 0) return false;

        return RunOnSta(() =>
        {
            using var server = new LocalPrintServer();
            using var queue = server.GetPrintQueue(printerName);
            var doc = BuildFixedDocument(pages);
            var writer = PrintQueue.CreateXpsDocumentWriter(queue);
            writer.Write(doc);
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
