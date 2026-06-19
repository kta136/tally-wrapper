namespace ShowroomBilling.Desktop.Services;

public interface IPrintPreferencesStore
{
    event EventHandler<double>? PrintPreviewZoomPercentChanged;

    string? LastPrinterName { get; }
    string? LastPdfDirectory { get; }
    bool DirectPrintAfterSave { get; }
    double PrintPreviewZoomPercent { get; }
    PrintJobSettings PrintJobSettings { get; }

    void SaveLastPrinter(string printerName);
    void SaveLastPdfDirectory(string directory);
    void SaveDirectPrintAfterSave(bool value);
    void SavePrintPreviewZoomPercent(double value);
    void SavePrintJobSettings(PrintJobSettings settings);
}
