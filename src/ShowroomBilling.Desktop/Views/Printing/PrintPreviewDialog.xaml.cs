using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ShowroomBilling.Desktop.ViewModels;

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
        }));
    }

    private void OnResizeThumbDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (Window.GetWindow(this)?.DataContext is not MainWindowViewModel root) return;
        var preview = root.PrintPreview;

        preview.DialogWidth = Math.Clamp(preview.DialogWidth + e.HorizontalChange, 760, Math.Max(760, ActualWidth - 24));
        preview.DialogHeight = Math.Clamp(preview.DialogHeight + e.VerticalChange, 520, Math.Max(520, ActualHeight - 24));
    }
}
