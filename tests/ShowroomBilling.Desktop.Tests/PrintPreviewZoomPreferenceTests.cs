using System.Windows.Media;
using System.Windows.Media.Imaging;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.Tests.TestSupport;
using ShowroomBilling.Desktop.ViewModels.Printing;
using ShowroomBilling.Desktop.ViewModels.Settings;

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
    public void PrintPreview_fitted_zoom_can_show_whole_page_without_overwriting_preference()
    {
        var preferences = new FakePrintPreferencesStore { PrintPreviewZoomPercent = 80 };
        var vm = new PrintPreviewViewModel(dispatcher: null, preferences);

        vm.ApplyFittedZoomPercent(38);

        Assert.Equal(38, vm.PreviewZoomPercent);
        Assert.Equal(80, preferences.PrintPreviewZoomPercent);
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
}
