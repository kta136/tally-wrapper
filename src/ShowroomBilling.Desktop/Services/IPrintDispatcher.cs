using ShowroomBilling.Printing;

namespace ShowroomBilling.Desktop.Services;

public interface IPrintDispatcher
{
    /// <summary>The printers Windows had at the last read (the first call reads them).</summary>
    IReadOnlyList<string> AvailablePrinters();
    string? DefaultPrinter();

    /// <summary>
    /// Reads the printers and the default printer from Windows again, so that
    /// one added, removed or made the default while the app runs is seen. Can
    /// take a moment: call it off the UI thread.
    /// </summary>
    void RefreshPrinters();
    PrintJobCapabilities GetPrinterCapabilities(string printerName);
    byte[] GeneratePdf(PrintDocumentOptions options);
    IReadOnlyList<byte[]> GeneratePageImages(PrintDocumentOptions options, int dpi = 150);
    IReadOnlyList<byte[]> GeneratePrintPageImages(IReadOnlyList<PrintDocumentOptions> options);
    bool PrintToPrinter(PrintDocumentOptions options, string printerName, PrintJobSettings? settings = null);
    bool PrintToPrinter(IReadOnlyList<PrintDocumentOptions> options, string printerName, PrintJobSettings? settings = null);
    bool PrintRenderedPages(IReadOnlyList<byte[]> pageImages, string printerName, PrintJobSettings? settings = null);
}
