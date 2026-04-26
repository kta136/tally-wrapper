using ShowroomBilling.Printing;

namespace ShowroomBilling.Desktop.Services;

public interface IPrintDispatcher
{
    IReadOnlyList<string> AvailablePrinters();
    string? DefaultPrinter();
    byte[] GeneratePdf(PrintDocumentOptions options);
    IReadOnlyList<byte[]> GeneratePageImages(PrintDocumentOptions options, int dpi = 150);
    bool PrintToPrinter(PrintDocumentOptions options, string printerName);
    string SavePdfToDisk(PrintDocumentOptions options, string directory, string fileNameWithoutExtension);
}
