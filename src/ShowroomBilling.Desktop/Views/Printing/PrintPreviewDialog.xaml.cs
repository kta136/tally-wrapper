using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.ViewModels;
using ShowroomBilling.Desktop.ViewModels.Printing;

namespace ShowroomBilling.Desktop.Views.Printing;

public partial class PrintPreviewDialog : UserControl
{
    private PrintPreviewViewModel? _subscribedPreview;
    private bool _fitPageWhenRendered;

    public PrintPreviewDialog()
    {
        InitializeComponent();
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible)
        {
            DetachPreview();
            return;
        }

        AttachPreview();
        _fitPageWhenRendered = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            PrintButton.Focus();
            FitPreviewToPage();
        }), DispatcherPriority.Loaded);
    }

    private void OnFitPageClick(object sender, RoutedEventArgs e)
    {
        _fitPageWhenRendered = true;
        FitPreviewToPage();
    }

    private void FitPreviewToPage()
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
        var pageHeight = firstPage.Height;
        var availableWidth = Math.Max(240, PreviewViewport.ActualWidth - 42);
        var availableHeight = Math.Max(240, PreviewViewport.ActualHeight - 42);
        if (pageWidth <= 0 || pageHeight <= 0 || availableWidth <= 0 || availableHeight <= 0)
        {
            return;
        }

        var fittedZoom = Math.Min(availableWidth / pageWidth, availableHeight / pageHeight) * 100.0;
        preview.ApplyFittedZoomPercent(fittedZoom);
        _fitPageWhenRendered = false;
    }

    private void AttachPreview()
    {
        var preview = PreviewList.DataContext as PrintPreviewViewModel;
        if (ReferenceEquals(preview, _subscribedPreview))
        {
            return;
        }

        DetachPreview();
        _subscribedPreview = preview;
        if (_subscribedPreview is not null)
        {
            _subscribedPreview.PreviewPages.CollectionChanged += OnPreviewPagesChanged;
        }
    }

    private void DetachPreview()
    {
        if (_subscribedPreview is not null)
        {
            _subscribedPreview.PreviewPages.CollectionChanged -= OnPreviewPagesChanged;
            _subscribedPreview = null;
        }
    }

    private void OnPreviewPagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!IsVisible
            || !_fitPageWhenRendered
            || _subscribedPreview is null
            || _subscribedPreview.PreviewPages.Count == 0)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(FitPreviewToPage), DispatcherPriority.Loaded);
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
