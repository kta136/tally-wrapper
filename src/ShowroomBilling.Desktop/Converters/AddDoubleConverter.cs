using System;
using System.Globalization;
using System.Windows.Data;

namespace ShowroomBilling.Desktop.Converters;

/// <summary>
/// Adds the converter parameter to a double. A drop-down list uses it to be as wide as the
/// box that opened it plus the room its shadow needs, which XAML cannot add up on its own.
/// </summary>
public sealed class AddDoubleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var amount = value is double number ? number : 0d;
        var extra = parameter is null
            ? 0d
            : double.Parse(parameter.ToString()!, CultureInfo.InvariantCulture);
        return Math.Max(0d, amount + extra);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
