using System.IO;
using System.Text.Json;

namespace ShowroomBilling.Desktop.Services;

public sealed class PrintPreferencesStore : IPrintPreferencesStore
{
    private const double DefaultZoomPercent = 100;
    private const double MinZoomPercent = 50;
    private const double MaxZoomPercent = 200;

    private readonly string _filePath;
    private PrintPreferencesData _data;

    public PrintPreferencesStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ShowroomBilling");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "print-preferences.json");
        _data = Load() ?? new PrintPreferencesData();
    }

    public event EventHandler<double>? PrintPreviewZoomPercentChanged;

    public string? LastPrinterName => string.IsNullOrWhiteSpace(_data.LastPrinterName) ? null : _data.LastPrinterName;
    public string? LastPdfDirectory => string.IsNullOrWhiteSpace(_data.LastPdfDirectory) ? null : _data.LastPdfDirectory;
    public bool DirectPrintAfterSave => _data.DirectPrintAfterSave;
    public double PrintPreviewZoomPercent => NormalizeZoom(_data.PrintPreviewZoomPercent);

    public void SaveLastPrinter(string printerName)
    {
        _data = _data with { LastPrinterName = printerName };
        Persist();
    }

    public void SaveLastPdfDirectory(string directory)
    {
        _data = _data with { LastPdfDirectory = directory };
        Persist();
    }

    public void SaveDirectPrintAfterSave(bool value)
    {
        _data = _data with { DirectPrintAfterSave = value };
        Persist();
    }

    public void SavePrintPreviewZoomPercent(double value)
    {
        var normalized = NormalizeZoom(value);
        if (Math.Abs(PrintPreviewZoomPercent - normalized) < 0.01) return;

        _data = _data with { PrintPreviewZoomPercent = normalized };
        Persist();
        PrintPreviewZoomPercentChanged?.Invoke(this, normalized);
    }

    private PrintPreferencesData? Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return null;
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<PrintPreferencesData>(json);
        }
        catch
        {
            return null;
        }
    }

    private void Persist()
    {
        try
        {
            var json = JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        }
        catch
        {
            // preferences are best-effort — ignore write failures
        }
    }

    private static double NormalizeZoom(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0) return DefaultZoomPercent;
        return Math.Clamp(Math.Round(value), MinZoomPercent, MaxZoomPercent);
    }

    private sealed record PrintPreferencesData
    {
        public string? LastPrinterName { get; init; }
        public string? LastPdfDirectory { get; init; }
        public bool DirectPrintAfterSave { get; init; }
        public double PrintPreviewZoomPercent { get; init; } = DefaultZoomPercent;
    }
}
