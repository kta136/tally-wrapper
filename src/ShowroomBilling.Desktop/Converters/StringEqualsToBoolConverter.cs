using System;
using System.Globalization;
using System.Windows.Data;

namespace ShowroomBilling.Desktop.Converters;

/// <summary>
/// Two-way: true when the bound string equals the converter parameter, and ticking it sets the bound
/// string to the parameter. It lets a group of radio buttons share one string setting, each button
/// standing for one value; unticking sets nothing, because the group ticks the next one itself.
/// </summary>
public sealed class StringEqualsToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value as string, parameter as string, StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true && parameter is string chosen ? chosen : Binding.DoNothing;
}
