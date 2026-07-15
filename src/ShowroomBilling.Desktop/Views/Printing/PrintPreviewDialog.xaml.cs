using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.ViewModels;
using ShowroomBilling.Desktop.ViewModels.Printing;

namespace ShowroomBilling.Desktop.Views.Printing;

public partial class PrintPreviewDialog : UserControl
{
    public PrintPreviewDialog()
    {
        InitializeComponent();
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            PrintButton.Focus();
            FitPreviewToWidth();
        }));
    }

    private void OnFitWidthClick(object sender, RoutedEventArgs e) => FitPreviewToWidth();

    private void FitPreviewToWidth()
    {
        if (PreviewList.DataContext is not PrintPreviewViewModel preview)
        {
            return;
        }

        var firstPage = preview.PreviewPages.FirstOrDefault();
        if (firstPage is null)
        {
            return;
        }

        var pageWidth = firstPage.Width;
        var availableWidth = Math.Max(240, PreviewViewport.ActualWidth - 42);
        if (pageWidth <= 0 || availableWidth <= 0)
        {
            return;
        }

        preview.PreviewZoomPercent = Math.Clamp(availableWidth / pageWidth * 100.0, 50, 200);
    }

    private void OnResizeThumbDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (Window.GetWindow(this)?.DataContext is not MainWindowViewModel root) return;
        var preview = root.PrintPreview;

        preview.DialogWidth = Math.Clamp(preview.DialogWidth + e.HorizontalChange, 760, Math.Max(760, ActualWidth - 24));
        preview.DialogHeight = Math.Clamp(preview.DialogHeight + e.VerticalChange, 520, Math.Max(520, ActualHeight - 24));
    }

    private void OnPrintSettingSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo || combo.DataContext is not PrintPreviewViewModel preview)
        {
            return;
        }

        switch (combo.Tag as string)
        {
            case "Duplex" when combo.SelectedValue is PrintDuplexMode duplex:
                preview.SelectedDuplexMode = duplex;
                break;
            case "Color" when combo.SelectedValue is PrintColorMode color:
                preview.SelectedColorMode = color;
                break;
            case "Collation" when combo.SelectedValue is PrintCollationMode collation:
                preview.SelectedCollationMode = collation;
                break;
        }
    }
}
