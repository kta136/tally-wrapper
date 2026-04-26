using System;
using System.Globalization;
using System.Windows.Data;

namespace ShowroomBilling.Desktop.Converters;

public sealed class OneBasedIndexConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int i) return (i + 1).ToString("00", culture);
        return string.Empty;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
