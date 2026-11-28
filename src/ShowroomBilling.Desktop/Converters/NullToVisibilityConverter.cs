using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ShowroomBilling.Desktop.Converters;

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool ShowWhenNull { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var missing = value is null || ReferenceEquals(value, DependencyProperty.UnsetValue);
        return missing == ShowWhenNull ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
