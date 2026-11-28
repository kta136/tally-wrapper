using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Printing;

namespace ShowroomBilling.Desktop.Tests.TestSupport;

internal sealed class FakePrintPreferencesStore : IPrintPreferencesStore
{
    public event EventHandler<double>? PrintPreviewZoomPercentChanged;

    public string? LastPrinterName { get; set; }
    public string? LastPdfDirectory { get; private set; }
    public bool DirectPrintAfterSave { get; set; }
    public double PrintPreviewZoomPercent { get; set; } = 100;
    public PrintJobSettings PrintJobSettings { get; set; } = PrintJobSettings.Default;

    public void SaveLastPrinter(string printerName) => LastPrinterName = printerName;
    public void SaveLastPdfDirectory(string directory) => LastPdfDirectory = directory;
    public void SaveDirectPrintAfterSave(bool value) => DirectPrintAfterSave = value;
    public void SavePrintJobSettings(PrintJobSettings settings) => PrintJobSettings = settings;

    public void SavePrintPreviewZoomPercent(double value)
    {
        PrintPreviewZoomPercent = value;
        PrintPreviewZoomPercentChanged?.Invoke(this, value);
    }
}
