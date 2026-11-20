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

    /// <summary>
    /// Whether a job sent to this printer with these settings is printed on both sides of the
    /// paper: by the settings when they say, else by what the printer is set to in Windows.
    /// True also when that cannot be read. Several bills go as one job only when this is false:
    /// printed two-sided, one bill would start on the back of another's last page.
    /// </summary>
    bool PrintsOnBothSides(string printerName, PrintJobSettings? settings = null);
    byte[] GeneratePdf(PrintDocumentOptions options);
    IReadOnlyList<byte[]> GeneratePageImages(PrintDocumentOptions options, int dpi = 150);

    /// <summary>
    /// Sends the bills to the printer as one job, a page for each copy. The
    /// pages go as text and lines for the printer to draw; a bill that cannot
    /// be drawn that way exactly goes as 600 DPI pictures of its pages. False
    /// when there was nothing to print; a printer that cannot be reached throws.
    /// Takes as long as the printer's queue takes: call it off the UI thread.
    /// </summary>
    bool PrintToPrinter(PrintDocumentOptions options, string printerName, PrintJobSettings? settings = null);
    bool PrintToPrinter(IReadOnlyList<PrintDocumentOptions> options, string printerName, PrintJobSettings? settings = null);
}
