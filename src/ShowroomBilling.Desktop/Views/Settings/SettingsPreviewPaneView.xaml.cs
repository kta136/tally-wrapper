using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ShowroomBilling.Desktop.ViewModels.Settings;

namespace ShowroomBilling.Desktop.Views.Settings;

public partial class SettingsPreviewPaneView : UserControl
{
    public static readonly DependencyProperty HostWidthProperty = DependencyProperty.Register(
        nameof(HostWidth),
        typeof(double),
        typeof(SettingsPreviewPaneView),
        new PropertyMetadata(0d));

    public SettingsPreviewPaneView()
    {
        InitializeComponent();
    }

    public double HostWidth
    {
        get => (double)GetValue(HostWidthProperty);
        set => SetValue(HostWidthProperty, value);
    }

    private void OnPreviewResizeThumbDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (DataContext is not SettingsPreviewViewModel preview) return;

        var maxWidth = Math.Max(320, HostWidth - 280);
        preview.PaneWidth = Math.Clamp(preview.PaneWidth - e.HorizontalChange, 320, maxWidth);
    }
}
