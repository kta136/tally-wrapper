using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ShowroomBilling.Desktop.Converters;

public sealed class DialogVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var dialogName = value as string;
        var expected = parameter as string;
        return string.Equals(dialogName, expected, StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
