using ShowroomBilling.Printing;

namespace ShowroomBilling.Desktop.Services;

public interface IPrintDispatcher
{
    IReadOnlyList<string> AvailablePrinters();
    string? DefaultPrinter();
    PrintJobCapabilities GetPrinterCapabilities(string printerName);
    byte[] GeneratePdf(PrintDocumentOptions options);
    IReadOnlyList<byte[]> GeneratePageImages(PrintDocumentOptions options, int dpi = 150);
    IReadOnlyList<byte[]> GeneratePrintPageImages(IReadOnlyList<PrintDocumentOptions> options);
    bool PrintToPrinter(PrintDocumentOptions options, string printerName, PrintJobSettings? settings = null);
    bool PrintToPrinter(IReadOnlyList<PrintDocumentOptions> options, string printerName, PrintJobSettings? settings = null);
    bool PrintRenderedPages(IReadOnlyList<byte[]> pageImages, string printerName, PrintJobSettings? settings = null);
    string SavePdfToDisk(PrintDocumentOptions options, string directory, string fileNameWithoutExtension);
}
