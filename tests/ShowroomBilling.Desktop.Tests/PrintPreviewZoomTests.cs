using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.ViewModels.Printing;

namespace ShowroomBilling.Desktop.Tests;

public sealed class PrintPreviewZoomTests
{
    [Theory]
    [InlineData(30, 30)]
    [InlineData(49.6, 50)]
    [InlineData(10, 25)]
    [InlineData(250, 200)]
    [InlineData(0, 100)]
    [InlineData(double.NaN, 100)]
    public void Zoom_is_rounded_into_the_shared_range(double input, double expected)
    {
        Assert.Equal(expected, PrintPreviewZoom.Normalize(input));
    }

    [Fact]
    public void Zooming_out_below_fifty_percent_is_kept_not_snapped_back()
    {
        // The view allowed 25% while the store that saved the value clamped it
        // to 50%, so the store's change event pushed the view back to 50.
        var vm = new PrintPreviewViewModel();
        vm.PreviewZoomPercent = 50;

        vm.ZoomOutCommand.Execute(null);

        Assert.Equal(40, vm.PreviewZoomPercent);
        Assert.Equal(40, PrintPreviewZoom.Normalize(vm.PreviewZoomPercent));
    }
}
