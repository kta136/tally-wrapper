using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Printing;

namespace ShowroomBilling.Desktop.Tests.TestSupport;

internal class StubPrintDispatcher : IPrintDispatcher
{
    public virtual IReadOnlyList<string> AvailablePrinters() => [];
    public virtual string? DefaultPrinter() => null;
    public virtual void RefreshPrinters() { }
    public virtual PrintJobCapabilities GetPrinterCapabilities(string printerName) => PrintJobCapabilities.Unknown;
    public virtual bool PrintsOnBothSides(string printerName, PrintJobSettings? settings = null) => false;
    public virtual byte[] GeneratePdf(PrintDocumentOptions options) => [];
    public virtual IReadOnlyList<byte[]> GeneratePageImages(PrintDocumentOptions options, int dpi = 150) => [];
    public virtual bool PrintToPrinter(PrintDocumentOptions options, string printerName, PrintJobSettings? settings = null) => false;
    public virtual bool PrintToPrinter(IReadOnlyList<PrintDocumentOptions> options, string printerName, PrintJobSettings? settings = null) => false;
}
