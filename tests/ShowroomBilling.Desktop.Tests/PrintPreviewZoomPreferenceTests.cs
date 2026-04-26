using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.ViewModels.Printing;
using ShowroomBilling.Desktop.ViewModels.Settings;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ShowroomBilling.Desktop.Tests;

public sealed class PrintPreviewZoomPreferenceTests
{
    [Fact]
    public void PrintPreview_zoom_buttons_save_shared_zoom_preference()
    {
        var preferences = new FakePrintPreferencesStore { PrintPreviewZoomPercent = 100 };
        var vm = new PrintPreviewViewModel(dispatcher: null, preferences);

        vm.ZoomInCommand.Execute(null);

        Assert.Equal(110, vm.PreviewZoomPercent);
        Assert.Equal(110, preferences.PrintPreviewZoomPercent);
    }

    [Fact]
    public void SettingsPreview_uses_and_saves_shared_zoom_preference()
    {
        var preferences = new FakePrintPreferencesStore { PrintPreviewZoomPercent = 80 };
        var vm = new SettingsPreviewViewModel(
            draft: null,
            printLayout: null,
            settings: null,
            dispatcher: null,
            assets: null,
            preferences);

        Assert.Equal(80, vm.PreviewZoomPercent);

        vm.ZoomOutCommand.Execute(null);

        Assert.Equal(70, vm.PreviewZoomPercent);
        Assert.Equal(70, preferences.PrintPreviewZoomPercent);
    }

    [Fact]
    public void SettingsPreview_fit_width_does_not_overwrite_shared_zoom_preference()
    {
        var preferences = new FakePrintPreferencesStore { PrintPreviewZoomPercent = 80 };
        var vm = new SettingsPreviewViewModel(
            draft: null,
            printLayout: null,
            settings: null,
            dispatcher: null,
            assets: null,
            preferences)
        {
            PaneWidth = 472,
            PageCount = 1,
        };
        vm.PreviewPages.Add(BitmapSource.Create(
            pixelWidth: 800,
            pixelHeight: 1000,
            dpiX: 96,
            dpiY: 96,
            pixelFormat: PixelFormats.Bgra32,
            palette: null,
            pixels: new byte[800 * 1000 * 4],
            stride: 800 * 4));

        vm.FitWidthCommand.Execute(null);

        Assert.Equal(50, vm.PreviewZoomPercent);
        Assert.Equal(80, preferences.PrintPreviewZoomPercent);
    }

    private sealed class FakePrintPreferencesStore : IPrintPreferencesStore
    {
        public event EventHandler<double>? PrintPreviewZoomPercentChanged;

        public string? LastPrinterName { get; private set; }
        public string? LastPdfDirectory { get; private set; }
        public bool DirectPrintAfterSave { get; private set; }
        public double PrintPreviewZoomPercent { get; set; } = 100;

        public void SaveLastPrinter(string printerName) => LastPrinterName = printerName;
        public void SaveLastPdfDirectory(string directory) => LastPdfDirectory = directory;
        public void SaveDirectPrintAfterSave(bool value) => DirectPrintAfterSave = value;

        public void SavePrintPreviewZoomPercent(double value)
        {
            PrintPreviewZoomPercent = value;
            PrintPreviewZoomPercentChanged?.Invoke(this, value);
        }
    }
}
