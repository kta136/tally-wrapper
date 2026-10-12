using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ShowroomBilling.Desktop.Converters;

/// <summary>
/// Visible when the bound string equals the converter parameter, collapsed otherwise. A view uses it
/// to show a control for one value of a tone or a mode without a style trigger per control.
/// </summary>
public sealed class StringEqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value as string, parameter as string, StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
