using System.Windows;
using System.Windows.Controls;

namespace ShowroomBilling.Desktop.Views.Settings;

/// <summary>Three sample table rows drawn at a given row height and text size, for the density choice.</summary>
public partial class DensitySample : UserControl
{
    public static readonly DependencyProperty SampleRowHeightProperty = DependencyProperty.Register(
        nameof(SampleRowHeight), typeof(double), typeof(DensitySample), new PropertyMetadata(38d));

    public static readonly DependencyProperty SampleFontSizeProperty = DependencyProperty.Register(
        nameof(SampleFontSize), typeof(double), typeof(DensitySample), new PropertyMetadata(13.5));

    public DensitySample()
    {
        InitializeComponent();
    }

    public double SampleRowHeight
    {
        get => (double)GetValue(SampleRowHeightProperty);
        set => SetValue(SampleRowHeightProperty, value);
    }

    public double SampleFontSize
    {
        get => (double)GetValue(SampleFontSizeProperty);
        set => SetValue(SampleFontSizeProperty, value);
    }
}
